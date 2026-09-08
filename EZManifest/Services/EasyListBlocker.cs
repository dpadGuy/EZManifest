using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace EZManifest.Services;

public sealed class EasyListBlocker
{
    private const string ListUrl = "https://easylist.to/easylist/easylist.txt";
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(4);

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly HashSet<string> _blockedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HostPathRule> _blockedPaths = [];
    private readonly List<HostPathRule> _allowedPaths = [];
    private readonly List<SubstringRule> _blockedSnippets = [];
    private readonly List<SubstringRule> _allowedSnippets = [];
    private readonly List<string> _genericHide = [];
    private readonly Dictionary<string, List<string>> _domainHide = new(StringComparer.OrdinalIgnoreCase);

    private bool _loaded;
    private string _genericCss = string.Empty;

    public EasyListBlocker(HttpClient httpClient) => _httpClient = httpClient;

    public bool IsReady => _loaded;

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
                return;

            string path = Path.Combine(AppPaths.DataDirectory, "easylist.txt");
            string text = await ReadOrDownloadAsync(path, cancellationToken);
            Parse(text);
            _genericCss = BuildCss(_genericHide);
            _loaded = true;
            AppLog.Write(
                $"[EasyList] Loaded hosts={_blockedHosts.Count} paths={_blockedPaths.Count} " +
                $"snippets={_blockedSnippets.Count} hide={_genericHide.Count}+{_domainHide.Count} domains");
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool ShouldBlock(string uri, CoreWebView2WebResourceContext context, string? pageHost)
    {
        if (!_loaded || !Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed))
            return false;

        if (parsed.Scheme is not ("http" or "https"))
            return false;

        var request = new Request(parsed, context, pageHost);
        if (IsAllowed(request))
            return false;

        return IsBlocked(request);
    }

    public string GetHideScript(string? pageHost)
    {
        if (!_loaded)
            return string.Empty;

        var css = new StringBuilder(_genericCss);
        if (!string.IsNullOrWhiteSpace(pageHost))
        {
            foreach (string domain in EnumerateParents(pageHost))
            {
                if (_domainHide.TryGetValue(domain, out List<string>? selectors))
                    css.Append(BuildCss(selectors));
            }
        }

        if (css.Length == 0)
            return string.Empty;

        return
            "(function(){var s=document.getElementById('ez-easylist');" +
            "if(!s){s=document.createElement('style');s.id='ez-easylist';" +
            "(document.documentElement||document.head).appendChild(s);}" +
            "s.textContent=" + JsonSerializer.Serialize(css.ToString()) + ";})();";
    }

    private async Task<string> ReadOrDownloadAsync(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < RefreshAfter)
            return await File.ReadAllTextAsync(path, cancellationToken);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ListUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", "EZManifest EasyList test");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text, cancellationToken);
            AppLog.Write("[EasyList] Downloaded latest list");
            return text;
        }
        catch (Exception ex) when (File.Exists(path))
        {
            AppLog.Write(ex, "EasyList download failed; using cached list");
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
    }

    private void Parse(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '!' or '[')
                continue;

            if (line.Contains("##", StringComparison.Ordinal) || line.Contains("#@#", StringComparison.Ordinal))
            {
                ParseCosmetic(line);
                continue;
            }

            if (line.Contains("#?#", StringComparison.Ordinal) || line.Contains("#$?#", StringComparison.Ordinal))
                continue;

            bool allow = line.StartsWith("@@", StringComparison.Ordinal);
            if (allow)
                line = line[2..];

            ParseNetwork(line, allow);
        }
    }

    private void ParseCosmetic(string line)
    {
        bool exception = line.Contains("#@#", StringComparison.Ordinal);
        if (exception)
            return;

        int sep = line.IndexOf("##", StringComparison.Ordinal);
        if (sep < 0)
            return;

        string domains = line[..sep];
        string selector = line[(sep + 2)..].Trim();
        if (selector.Length == 0 || !IsPlainCssSelector(selector))
            return;

        if (domains.Length == 0)
        {
            _genericHide.Add(selector);
            return;
        }

        foreach (string part in domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith('~') || part.Length == 0)
                continue;
            if (!_domainHide.TryGetValue(part, out List<string>? list))
            {
                list = [];
                _domainHide[part] = list;
            }

            list.Add(selector);
        }
    }

    private void ParseNetwork(string line, bool allow)
    {
        string pattern = line;
        string options = string.Empty;
        int dollar = line.LastIndexOf('$');
        if (dollar >= 0)
        {
            pattern = line[..dollar];
            options = line[(dollar + 1)..];
        }

        if (pattern.Length == 0)
            return;

        RuleOptions flags = ParseOptions(options);
        if (flags.Unsupported)
            return;

        if (pattern.StartsWith("||", StringComparison.Ordinal))
        {
            string rest = pattern[2..];
            int cut = rest.IndexOfAny(['^', '/', '|']);
            string host = cut < 0 ? rest.TrimEnd('^', '*') : rest[..cut];
            host = host.TrimEnd('.');
            if (host.Length == 0 || host.Contains('*'))
                return;

            string? path = null;
            if (cut >= 0 && rest[cut] == '/')
            {
                int end = rest.IndexOf('^', cut);
                path = end < 0 ? rest[cut..] : rest[cut..end];
            }

            if (string.IsNullOrEmpty(path) || path == "/")
            {
                if (flags.Types == ResourceTypes.All && !flags.ThirdParty && !flags.FirstParty && flags.Domains is null)
                {
                    (allow ? _allowedHosts : _blockedHosts).Add(host);
                    return;
                }

                (allow ? _allowedPaths : _blockedPaths).Add(new HostPathRule(host, "/", flags));
                return;
            }

            (allow ? _allowedPaths : _blockedPaths).Add(new HostPathRule(host, path, flags));
            return;
        }

        if (pattern.Length < 5)
            return;

        string snippet = pattern.Trim('|', '*', '^');
        if (snippet.Length < 5)
            return;

        (allow ? _allowedSnippets : _blockedSnippets).Add(new SubstringRule(snippet, flags));
    }

    private bool IsBlocked(Request request)
    {
        if (HostOrParentIn(_blockedHosts, request.Host))
            return OptionAllows(default, request);

        foreach (HostPathRule rule in _blockedPaths)
        {
            if (HostMatches(request.Host, rule.Host) &&
                request.Path.StartsWith(rule.Path, StringComparison.OrdinalIgnoreCase) &&
                OptionAllows(rule.Options, request))
            {
                return true;
            }
        }

        foreach (SubstringRule rule in _blockedSnippets)
        {
            if (request.Url.Contains(rule.Snippet, StringComparison.OrdinalIgnoreCase) &&
                OptionAllows(rule.Options, request))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsAllowed(Request request)
    {
        if (HostOrParentIn(_allowedHosts, request.Host))
            return true;

        foreach (HostPathRule rule in _allowedPaths)
        {
            if (HostMatches(request.Host, rule.Host) &&
                request.Path.StartsWith(rule.Path, StringComparison.OrdinalIgnoreCase) &&
                OptionAllows(rule.Options, request))
            {
                return true;
            }
        }

        foreach (SubstringRule rule in _allowedSnippets)
        {
            if (request.Url.Contains(rule.Snippet, StringComparison.OrdinalIgnoreCase) &&
                OptionAllows(rule.Options, request))
            {
                return true;
            }
        }

        return false;
    }

    private static bool OptionAllows(RuleOptions options, Request request)
    {
        if (options.ThirdParty && !request.ThirdParty)
            return false;
        if (options.FirstParty && request.ThirdParty)
            return false;
        if (options.Types != ResourceTypes.All && (options.Types & ToType(request.Context)) == 0)
            return false;
        if (options.Domains is { Count: > 0 } && request.PageHost is not null)
        {
            bool hit = false;
            foreach (string domain in options.Domains)
            {
                if (HostMatches(request.PageHost, domain))
                {
                    hit = true;
                    break;
                }
            }

            if (!hit)
                return false;
        }

        return true;
    }

    private static ResourceTypes ToType(CoreWebView2WebResourceContext context) => context switch
    {
        CoreWebView2WebResourceContext.Image => ResourceTypes.Image,
        CoreWebView2WebResourceContext.Media => ResourceTypes.Media,
        CoreWebView2WebResourceContext.Script => ResourceTypes.Script,
        CoreWebView2WebResourceContext.Stylesheet => ResourceTypes.Style,
        CoreWebView2WebResourceContext.Document => ResourceTypes.Document,
        CoreWebView2WebResourceContext.XmlHttpRequest => ResourceTypes.Xhr,
        CoreWebView2WebResourceContext.Fetch => ResourceTypes.Xhr,
        CoreWebView2WebResourceContext.Font => ResourceTypes.Font,
        _ => ResourceTypes.Other
    };

    private static RuleOptions ParseOptions(string options)
    {
        var flags = new RuleOptions();
        if (string.IsNullOrWhiteSpace(options))
            return flags;

        foreach (string raw in options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string option = raw.ToLowerInvariant();
            switch (option)
            {
                case "third-party":
                    flags.ThirdParty = true;
                    break;
                case "~third-party":
                case "first-party":
                    flags.FirstParty = true;
                    break;
                case "image":
                    flags.Types |= ResourceTypes.Image;
                    break;
                case "script":
                    flags.Types |= ResourceTypes.Script;
                    break;
                case "stylesheet":
                    flags.Types |= ResourceTypes.Style;
                    break;
                case "media":
                    flags.Types |= ResourceTypes.Media;
                    break;
                case "document":
                    flags.Types |= ResourceTypes.Document;
                    break;
                case "xmlhttprequest":
                    flags.Types |= ResourceTypes.Xhr;
                    break;
                case "font":
                    flags.Types |= ResourceTypes.Font;
                    break;
                case "popup":
                case "websocket":
                case "ping":
                case "csp":
                    flags.Unsupported = true;
                    break;
                default:
                    if (option.StartsWith("domain=", StringComparison.Ordinal))
                    {
                        flags.Domains = [];
                        foreach (string domain in option[7..].Split('|', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (!domain.StartsWith('~'))
                                flags.Domains.Add(domain);
                        }
                    }

                    break;
            }
        }

        return flags;
    }

    private static bool HostOrParentIn(HashSet<string> set, string host)
    {
        if (set.Contains(host))
            return true;

        foreach (string parent in EnumerateParents(host))
        {
            if (set.Contains(parent))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateParents(string host)
    {
        yield return host;
        int index = 0;
        while ((index = host.IndexOf('.', index)) >= 0)
        {
            index++;
            if (index < host.Length)
                yield return host[index..];
        }
    }

    private static bool HostMatches(string host, string ruleHost) =>
        host.Equals(ruleHost, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith('.' + ruleHost, StringComparison.OrdinalIgnoreCase);

    private static bool IsPlainCssSelector(string selector)
    {
        if (selector.Contains(":has(", StringComparison.OrdinalIgnoreCase) ||
            selector.Contains(":-abp-", StringComparison.OrdinalIgnoreCase) ||
            selector.Contains(":xpath(", StringComparison.OrdinalIgnoreCase) ||
            selector.Contains("xmlhttprequest", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string BuildCss(IReadOnlyList<string> selectors)
    {
        if (selectors.Count == 0)
            return string.Empty;

        var builder = new StringBuilder(selectors.Count * 24);
        for (int i = 0; i < selectors.Count; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(selectors[i]);
        }

        builder.Append("{display:none!important}");
        return builder.ToString();
    }

    private readonly record struct Request(Uri Uri, CoreWebView2WebResourceContext Context, string? PageHost)
    {
        public string Host => Uri.Host;
        public string Path => Uri.AbsolutePath;
        public string Url => Uri.ToString();
        public bool ThirdParty =>
            !string.IsNullOrWhiteSpace(PageHost) &&
            !HostMatches(Host, PageHost);
    }

    private readonly record struct HostPathRule(string Host, string Path, RuleOptions Options);

    private readonly record struct SubstringRule(string Snippet, RuleOptions Options);

    private struct RuleOptions
    {
        public bool ThirdParty;
        public bool FirstParty;
        public bool Unsupported;
        public ResourceTypes Types;
        public List<string>? Domains;
    }

    [Flags]
    private enum ResourceTypes
    {
        All = 0,
        Image = 1,
        Script = 2,
        Style = 4,
        Media = 8,
        Document = 16,
        Xhr = 32,
        Font = 64,
        Other = 128
    }
}
