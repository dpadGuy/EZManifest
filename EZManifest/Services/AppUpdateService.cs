using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json.Nodes;
using EZManifest.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EZManifest.Services;

public sealed class AppUpdateService
{
    public const string GitHubOwner = "dpadGuy";
    public const string GitHubRepo = "EZManifest";

    private readonly HttpClient _httpClient;
    private readonly AppSettingsService _settingsService;
    private readonly AppMessageBoxService _messageBoxService;
    private readonly WindowProvider _windowProvider;

    public AppUpdateService(
        HttpClient httpClient,
        AppSettingsService settingsService,
        AppMessageBoxService messageBoxService,
        WindowProvider windowProvider)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _messageBoxService = messageBoxService;
        _windowProvider = windowProvider;
    }

    public static Version CurrentVersion { get; } =
        Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));

    public async Task<AppUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        AppLog.Write($"[Update] Checking {GitHubOwner}/{GitHubRepo} releases. Local {FormatVersion(CurrentVersion)}");

        JsonArray? releases = await FetchReleasesAsync(cancellationToken);
        if (releases is null || releases.Count == 0)
            return null;

        AppUpdateInfo? best = null;
        foreach (JsonNode? node in releases)
        {
            if (node is not JsonObject release)
                continue;

            if (!TryReadUpdate(release, out AppUpdateInfo? candidate) || candidate is null)
                continue;

            if (candidate.Version <= CurrentVersion)
            {
                AppLog.Write($"[Update] Skipping {candidate.TagName} ({candidate.Version}) — not newer than local");
                continue;
            }

            if (best is null || candidate.Version > best.Version)
                best = candidate;
        }

        if (best is null)
        {
            AppLog.Write($"[Update] Up to date. Local {CurrentVersion}");
            return null;
        }

        AppLog.Write($"[Update] Offering {best.TagName} ({best.Version}) hotfix={best.IsHotfix}");
        return best;
    }

    private async Task<JsonArray?> FetchReleasesAsync(CancellationToken cancellationToken)
    {
        string listUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases?per_page=30";
        using var request = new HttpRequestMessage(HttpMethod.Get, listUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("EZManifest", CurrentVersion.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Write($"[Update] GitHub releases returned {(int)response.StatusCode} {response.ReasonPhrase}");
            response.EnsureSuccessStatusCode();
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonNode.Parse(json) as JsonArray;
    }

    private static bool TryReadUpdate(JsonObject release, out AppUpdateInfo? update)
    {
        update = null;
        if (release["draft"]?.GetValue<bool>() == true)
            return false;

        string? tag = release["tag_name"]?.ToString();
        string? name = release["name"]?.ToString();
        bool githubPrerelease = release["prerelease"]?.GetValue<bool>() == true;

        if (IsIgnoredChannel(tag) || IsIgnoredChannel(name))
        {
            AppLog.Write($"[Update] Ignoring channel tag '{tag}'");
            return false;
        }

        bool hotfix = IsHotfixRelease(tag, name);
        if (githubPrerelease && !hotfix)
        {
            AppLog.Write($"[Update] Ignoring pre-release '{tag}'");
            return false;
        }

        if (!TryParseReleaseVersion(tag, hotfix, out Version remote, out int hotfixNumber, out bool parsedHotfix))
        {
            AppLog.Write($"[Update] Could not parse release tag '{tag}'");
            return false;
        }

        if (!TryFindInstaller(release["assets"] as JsonArray, out Uri? downloadUri, out string fileName) ||
            downloadUri is null)
        {
            AppLog.Write($"[Update] Release '{tag}' has no EZManifest-Setup*.exe asset");
            return false;
        }

        update = new AppUpdateInfo
        {
            Version = remote,
            TagName = tag ?? remote.ToString(),
            DownloadUri = downloadUri,
            FileName = fileName,
            ReleaseNotes = TrimNotes(release["body"]?.ToString()),
            ReleasePageUri = TryCreateUri(release["html_url"]?.ToString()),
            IsHotfix = hotfix || parsedHotfix,
            HotfixNumber = hotfixNumber
        };
        return true;
    }

    public async Task PromptIfAvailableAsync(bool silentWhenCurrent, CancellationToken cancellationToken = default)
    {
        AppUpdateInfo? update;
        try
        {
            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            checkTimeout.CancelAfter(TimeSpan.FromSeconds(12));
            update = await CheckForUpdateAsync(checkTimeout.Token);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "[Update] Check failed");
            if (!silentWhenCurrent)
            {
                await _messageBoxService.ShowAsync(
                    "Could not check for updates",
                    ex is TaskCanceledException or OperationCanceledException
                        ? "The update check timed out. Try again when you have a connection."
                        : ex.Message);
            }

            return;
        }

        if (update is null)
        {
            if (!silentWhenCurrent)
            {
                await _messageBoxService.ShowAsync(
                    "You're up to date",
                    $"EZManifest {FormatVersion(CurrentVersion)} is the latest release.");
            }

            return;
        }

        using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        downloadTimeout.CancelAfter(TimeSpan.FromMinutes(5));
        await OfferUpdateAsync(update, downloadTimeout.Token);
    }

    private async Task OfferUpdateAsync(AppUpdateInfo update, CancellationToken cancellationToken)
    {
        var result = await ShowUpdatePromptAsync(update);

        if (result == ContentDialogResult.Secondary)
        {
            await _settingsService.UpdateAsync(settings => settings.CheckForUpdatesOnStartup = false);
            AppLog.Write("[Update] User chose never ask again");
            return;
        }

        if (result != ContentDialogResult.Primary)
        {
            AppLog.Write($"[Update] User declined {update.TagName}");
            return;
        }

        string installerPath;
        try
        {
            installerPath = await DownloadInstallerWithProgressAsync(update, cancellationToken);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "[Update] Download failed");
            await _messageBoxService.ShowAsync(
                "Update failed",
                $"Could not download the installer.\n\n{ex.Message}");
            return;
        }

        string relaunchExe = ResolveRunningExePath();
        string installDir = Path.GetDirectoryName(relaunchExe) ?? AppPaths.ExeDirectory;
        installDir = installDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        AppLog.Write($"[Update] Silent install into '{installDir}' (running {relaunchExe})");
        StartSilentInstall(installerPath, installDir, relaunchExe);

        if (_windowProvider.Window is MainWindow mainWindow)
            mainWindow.CloseForUpdate();
    }

    private async Task<string> DownloadInstallerWithProgressAsync(
        AppUpdateInfo update,
        CancellationToken cancellationToken)
    {
        var statusText = new TextBlock
        {
            Text = $"EZManifest will soon close and open for the update to take effect\nDownloading {update.FileName}...",
            TextWrapping = TextWrapping.WrapWholeWords
        };
        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 8,
            IsIndeterminate = true
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(statusText);
        content.Children.Add(progressBar);

        var dialog = new ContentDialog
        {
            Title = "Downloading update",
            Content = content,
            XamlRoot = ResolveXamlRoot(),
            RequestedTheme = ResolveTheme(),
            CloseButtonText = "Please wait..."
        };
        dialog.Resources["ContentDialogMinWidth"] = 420.0;
        dialog.Resources["ContentDialogMaxWidth"] = 560.0;

        bool finished = false;
        dialog.Closing += (_, args) =>
        {
            if (!finished)
                args.Cancel = true;
        };

        var showTask = dialog.ShowAsync().AsTask();
        try
        {
            string path = await DownloadInstallerAsync(update, (received, total) =>
            {
                if (total is > 0)
                {
                    progressBar.IsIndeterminate = false;
                    progressBar.Maximum = 100;
                    progressBar.Value = Math.Clamp(received * 100.0 / total.Value, 0, 100);
                    statusText.Text =
                        $"EZManifest will soon close and open for the update to take effect\nDownloading {update.FileName}\n{FormatBytes(received)} / {FormatBytes(total.Value)}";
                }
                else
                {
                    progressBar.IsIndeterminate = true;
                    statusText.Text = $"EZManifest will soon close and open for the update to take effect\nDownloading {update.FileName}\n{FormatBytes(received)}";
                }
            }, cancellationToken);

            progressBar.IsIndeterminate = false;
            progressBar.Value = 100;
            statusText.Text = "Download complete.";
            return path;
        }
        finally
        {
            finished = true;
            try
            {
                dialog.Hide();
                await showTask;
            }
            catch
            {
            }
        }
    }

    private async Task<string> DownloadInstallerAsync(
        AppUpdateInfo update,
        Action<long, long?> report,
        CancellationToken cancellationToken)
    {
        string folder = Path.Combine(Path.GetTempPath(), "EZManifest", "updates");
        Directory.CreateDirectory(folder);

        string path = Path.Combine(folder, update.FileName);
        if (update.DownloadUri.IsFile)
        {
            File.Copy(update.DownloadUri.LocalPath, path, overwrite: true);
            long size = new FileInfo(path).Length;
            report(size, size);
            return path;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, update.DownloadUri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("EZManifest", CurrentVersion.ToString()));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous);

        byte[] buffer = new byte[81920];
        long received = 0;
        long lastReport = 0;
        report(0, total);

        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            if (received - lastReport < 256 * 1024 && (total is null || received < total.Value))
                continue;

            lastReport = received;
            report(received, total);
        }

        report(received, total ?? received);
        return path;
    }

    private async Task<ContentDialogResult> ShowUpdatePromptAsync(AppUpdateInfo update)
    {
        ContentDialogResult chosen = ContentDialogResult.None;
        var dialog = new ContentDialog
        {
            Title = "Update available",
            XamlRoot = ResolveXamlRoot(),
            RequestedTheme = ResolveTheme()
        };
        dialog.Resources["ContentDialogMinWidth"] = 480.0;
        dialog.Resources["ContentDialogMaxWidth"] = 560.0;
        dialog.Resources["ContentDialogMinHeight"] = 0.0;

        var ok = CreateEqualDialogButton("OK");
        var never = CreateEqualDialogButton("Never ask me again");
        var notNow = CreateEqualDialogButton("Not now");
        ok.Click += (_, _) =>
        {
            chosen = ContentDialogResult.Primary;
            dialog.Hide();
        };
        notNow.Click += (_, _) =>
        {
            chosen = ContentDialogResult.None;
            dialog.Hide();
        };
        never.Click += (_, _) =>
        {
            chosen = ContentDialogResult.Secondary;
            dialog.Hide();
        };

        var buttons = new Grid { ColumnSpacing = 8 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(ok, 0);
        Grid.SetColumn(notNow, 1);
        Grid.SetColumn(never, 2);
        buttons.Children.Add(ok);
        buttons.Children.Add(notNow);
        buttons.Children.Add(never);

        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(new TextBlock
        {
            Text = $"EZManifest {FormatUpdateLabel(update)} is available.\nWould you like to update ?",
            TextWrapping = TextWrapping.WrapWholeWords
        });
        body.Children.Add(buttons);
        dialog.Content = body;

        await dialog.ShowAsync();
        return chosen;
    }

    private static Button CreateEqualDialogButton(string text) =>
        new()
        {
            Content = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(8, 6, 8, 6)
        };

    private XamlRoot ResolveXamlRoot()
    {
        if (_windowProvider.Window.Content is FrameworkElement root && root.XamlRoot is not null)
            return root.XamlRoot;

        throw new InvalidOperationException("Main window XamlRoot is not available.");
    }

    private ElementTheme ResolveTheme()
    {
        if (_windowProvider.Window.Content is FrameworkElement root)
            return root.ActualTheme;

        return ElementTheme.Default;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private static string ResolveRunningExePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
            return Path.GetFullPath(processPath);

        return Path.Combine(AppPaths.ExeDirectory, "EZManifest.exe");
    }

    private static void StartSilentInstall(string installerPath, string installDir, string relaunchExe)
    {
        string arguments =
            $"/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /DIR=\"{installDir}\"";
        string command =
            $"start /wait \"\" \"{installerPath}\" {arguments} && start \"\" \"{relaunchExe}\"";
        AppLog.Write($"[Update] {command}");
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + command,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static bool TryFindInstaller(JsonArray? assets, out Uri? downloadUri, out string fileName)
    {
        downloadUri = null;
        fileName = string.Empty;
        if (assets is null)
            return false;

        JsonNode? match = assets.FirstOrDefault(asset =>
            IsSetupAsset(asset?["name"]?.ToString()))
            ?? assets.FirstOrDefault(asset =>
                asset?["name"]?.ToString()?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);

        string? name = match?["name"]?.ToString();
        string? url = match?["browser_download_url"]?.ToString();
        if (string.IsNullOrWhiteSpace(name) || !TryCreateUri(url, out downloadUri) || downloadUri is null)
            return false;

        fileName = name;
        return true;
    }

    private static bool IsSetupAsset(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && name.Contains("Setup", StringComparison.OrdinalIgnoreCase);

    private static bool IsHotfixRelease(string? tag, string? name) =>
        IsHotfixLabel(tag) || IsHotfixLabel(name);

    private static bool IsHotfixLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("hotfix", StringComparison.OrdinalIgnoreCase)
            || HasHotfixSuffix(StripLeadingV(value));
    }

    private static bool HasHotfixSuffix(string text)
    {
        int dash = text.IndexOf('-');
        if (dash < 0 || dash >= text.Length - 1)
            return false;

        return IsHotfixSuffix(text[(dash + 1)..]);
    }

    private static bool IsHotfixSuffix(string suffix) =>
        TryReadHotfixNumber(suffix, out _);

    private static bool TryReadHotfixNumber(string suffix, out int number)
    {
        number = 0;
        suffix = suffix.Trim();
        foreach (string prefix in new[] { "hotfix", "hf" })
        {
            if (!suffix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string rest = suffix[prefix.Length..];
            if (rest.Length == 0)
            {
                number = 1;
                return true;
            }

            if (rest[0] is '.' or '-')
                rest = rest[1..];

            if (rest.Length == 0)
            {
                number = 1;
                return true;
            }

            if (int.TryParse(rest, out int parsed) && parsed > 0)
            {
                number = parsed;
                return true;
            }
        }

        return false;
    }

    private static bool IsIgnoredChannel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("beta", StringComparison.OrdinalIgnoreCase)
            || value.Contains("alpha", StringComparison.OrdinalIgnoreCase)
            || value.Contains("preview", StringComparison.OrdinalIgnoreCase)
            || value.Contains("nightly", StringComparison.OrdinalIgnoreCase)
            || value.Contains("canary", StringComparison.OrdinalIgnoreCase)
            || ContainsRcChannel(value);
    }

    private static bool ContainsRcChannel(string value)
    {
        int index = value.IndexOf("rc", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return false;

        bool startOk = index == 0 || !char.IsLetter(value[index - 1]);
        int end = index + 2;
        bool endOk = end >= value.Length || !char.IsLetter(value[end]);
        return startOk && endOk;
    }

    private static bool TryParseReleaseVersion(
        string? tag,
        bool namedHotfix,
        out Version version,
        out int hotfixNumber,
        out bool hotfix)
    {
        version = new Version(0, 0, 0, 0);
        hotfixNumber = 0;
        hotfix = false;
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        string text = StripLeadingV(tag.Trim());
        int dash = text.IndexOf('-');
        string core = dash < 0 ? text : text[..dash];
        string suffix = dash < 0 ? string.Empty : text[(dash + 1)..];

        if (!Version.TryParse(core, out Version? parsed))
            return false;

        parsed = Normalize(parsed);
        if (string.IsNullOrEmpty(suffix))
        {
            if (!namedHotfix)
            {
                version = parsed;
                return true;
            }

            hotfix = true;
            hotfixNumber = 1;
            version = new Version(parsed.Major, parsed.Minor, parsed.Build, 1);
            return true;
        }

        if (!TryReadHotfixNumber(suffix, out hotfixNumber))
            return false;

        hotfix = true;
        version = new Version(parsed.Major, parsed.Minor, parsed.Build, hotfixNumber);
        return true;
    }

    private static string StripLeadingV(string text) =>
        text.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? text[1..] : text;

    private static string FormatUpdateLabel(AppUpdateInfo update)
    {
        string core = $"{update.Version.Major}.{update.Version.Minor}.{update.Version.Build}";
        if (!update.IsHotfix)
            return core;

        return update.HotfixNumber <= 1 ? $"{core} hotfix" : $"{core} hotfix {update.HotfixNumber}";
    }

    private static Version Normalize(Version version) =>
        new(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            version.Build < 0 ? 0 : version.Build,
            version.Revision < 0 ? 0 : version.Revision);

    private static string FormatVersion(Version version) =>
        version.Revision == 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : version.ToString();

    private static string TrimNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        string text = body.Replace("\r\n", "\n").Trim();
        const int max = 360;
        if (text.Length <= max)
            return text;

        return text[..max].TrimEnd() + "…";
    }

    private static Uri? TryCreateUri(string? value) =>
        TryCreateUri(value, out Uri? uri) ? uri : null;

    private static bool TryCreateUri(string? value, out Uri? uri)
    {
        uri = null;
        return !string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value, UriKind.Absolute, out uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }
}
