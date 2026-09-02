using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EZManifest.Models;

namespace EZManifest.Services;

public sealed class SteamMetadataService
{
    private readonly HttpClient _httpClient;

    public SteamMetadataService(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<string> GetGameNameAsync(string appId, CancellationToken cancellationToken = default)
    {
        string response = await _httpClient.GetStringAsync(
            $"https://store.steampowered.com/api/appdetails?appids={appId}",
            cancellationToken);
        var root = JsonNode.Parse(response);
        return root?[appId]?["data"]?["name"]?.ToString() ?? $"Steam App {appId}";
    }

    public async Task<string?> GetAboutTheGameAsync(string appId, CancellationToken cancellationToken = default)
    {
        SteamStorePageInfo info = await GetStorePageInfoAsync(appId, cancellationToken);
        return info.AboutTheGame;
    }

    /// <summary>
    /// Store about-text plus screenshot/trailer URLs. Media is not saved locally —
    /// callers should bind Image/MediaPlayer sources to these Valve CDN URIs.
    /// </summary>
    public async Task<SteamStorePageInfo> GetStorePageInfoAsync(
        string appId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return new SteamStorePageInfo();

        string response = await _httpClient.GetStringAsync(
            $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english",
            cancellationToken);
        var data = JsonNode.Parse(response)?[appId]?["data"];
        if (data is null)
            return new SteamStorePageInfo();

        string? html = FirstNonEmpty(
            data["about_the_game"]?.ToString(),
            data["detailed_description"]?.ToString(),
            data["short_description"]?.ToString());
        string text = StripSteamHtml(html);
        string? about = string.IsNullOrWhiteSpace(text) ? null : text;

        return new SteamStorePageInfo
        {
            AboutTheGame = about,
            Media = ParseMedia(data)
        };
    }

    private static List<GameMediaItem> ParseMedia(JsonNode data)
    {
        var items = new List<GameMediaItem>();

        if (data["movies"] is JsonArray movies)
        {
            foreach (JsonNode? movie in movies.OrderByDescending(IsHighlightMovie))
            {
                if (movie is null)
                    continue;

                string? movieId = movie["id"]?.ToString();
                IReadOnlyList<Uri> videos = CollectMoviePlaybackUris(movie, movieId);
                string? thumbnail = PreferHttps(movie["thumbnail"]?.ToString());
                if (videos.Count == 0 || string.IsNullOrWhiteSpace(thumbnail))
                    continue;

                items.Add(CreateMediaItem(
                    isVideo: true,
                    thumbnail: thumbnail,
                    image: thumbnail,
                    videos: videos));
            }
        }

        if (data["screenshots"] is JsonArray screenshots)
        {
            foreach (JsonNode? shot in screenshots)
            {
                if (shot is null)
                    continue;

                string? full = PreferHttps(shot["path_full"]?.ToString());
                string? thumbnail = PreferHttps(shot["path_thumbnail"]?.ToString()) ?? full;
                if (string.IsNullOrWhiteSpace(full) && string.IsNullOrWhiteSpace(thumbnail))
                    continue;

                items.Add(CreateMediaItem(
                    isVideo: false,
                    thumbnail: thumbnail ?? full!,
                    image: full ?? thumbnail!,
                    videos: []));
            }
        }

        return items;
    }

    /// <summary>
    /// MediaPlayerElement can play progressive H.264 MP4 and H.264 HLS/DASH.
    /// It cannot play WebM/VP9, and invented movie_max.mp4 URLs 404 on current Steam CDN.
    /// </summary>
    private static List<Uri> CollectMoviePlaybackUris(JsonNode movie, string? movieId)
    {
        var urls = new List<Uri>();
        AddMovieUrl(urls, movie["hls_h264"]?.ToString());
        AddMovieUrl(urls, movie["hls"]?.ToString());
        AddMovieUrl(urls, movie["dash_h264"]?.ToString());
        AddMovieUrl(urls, movie["mp4"]?["max"]?.ToString());
        AddMovieUrl(urls, movie["mp4"]?["480"]?.ToString());
        AddMovieUrl(urls, movie["dash_av1"]?.ToString());

        if (urls.Count == 0)
        {
            AddMovieUrl(urls, ProgressiveMovieUrl(movieId, "movie_max.mp4"));
            AddMovieUrl(urls, ProgressiveMovieUrl(movieId, "movie480.mp4"));
        }

        return urls;
    }

    private static void AddMovieUrl(List<Uri> urls, string? url)
    {
        Uri? uri = TryCreateUri(PreferHttps(url));
        if (uri is null)
            return;

        foreach (Uri existing in urls)
        {
            if (Uri.Compare(
                    existing,
                    uri,
                    UriComponents.HttpRequestUrl,
                    UriFormat.Unescaped,
                    StringComparison.OrdinalIgnoreCase) == 0)
            {
                return;
            }
        }

        urls.Add(uri);
    }

    private static GameMediaItem CreateMediaItem(
        bool isVideo,
        string thumbnail,
        string image,
        IReadOnlyList<Uri> videos)
    {
        Uri? video = videos.Count > 0 ? videos[0] : null;
        return new GameMediaItem
        {
            IsVideo = isVideo,
            ThumbnailUrl = thumbnail,
            ImageUrl = image,
            VideoUrl = video?.ToString(),
            ThumbnailUri = TryCreateUri(thumbnail),
            ImageUri = TryCreateUri(image),
            VideoUri = video,
            VideoUris = videos
        };
    }

    private static bool IsHighlightMovie(JsonNode? movie)
    {
        JsonNode? highlight = movie?["highlight"];
        if (highlight is null)
            return false;

        try
        {
            return highlight.GetValue<bool>();
        }
        catch
        {
            return highlight.ToString() is "1" or "true";
        }
    }

    private static string? ProgressiveMovieUrl(string? movieId, string fileName) =>
        string.IsNullOrWhiteSpace(movieId)
            ? null
            : $"https://cdn.akamai.steamstatic.com/steam/apps/{movieId}/{fileName}";

    private static string? PreferHttps(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url[7..]
            : url;
    }

    private static Uri? TryCreateUri(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            ? uri
            : null;

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string StripSteamHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        string text = html;
        text = Regex.Replace(text, @"<(br|BR)\s*/?>", "\n");
        text = Regex.Replace(text, @"</p>", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</h[1-6]>", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</li>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<li[^>]*>", "• ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, @"[ \t\u00a0]+\n", "\n");
        text = Regex.Replace(text, @"\n[ \t\u00a0]+", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    public async Task DownloadArtworkAsync(
        string appId,
        string logoPath,
        string coverArtPath,
        string? heroPath = null,
        string? iconPath = null,
        CancellationToken cancellationToken = default)
    {
        await DownloadIfAvailableAsync(
            $"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/logo.png",
            logoPath,
            cancellationToken);
        await DownloadIfAvailableAsync(
            $"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
            coverArtPath,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(heroPath))
            await DownloadHeroAsync(appId, heroPath, cancellationToken);
        if (!string.IsNullOrWhiteSpace(iconPath))
            await DownloadIconAsync(appId, iconPath, cancellationToken);
    }

    public async Task<bool> DownloadIconAsync(
        string appId,
        string iconPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(iconPath))
            return false;

        if (File.Exists(iconPath) && new FileInfo(iconPath).Length > 0)
            return true;

        string? hash = await GetCommunityIconHashAsync(appId, cancellationToken);
        if (string.IsNullOrWhiteSpace(hash))
            return false;

        string[] urls =
        [
            $"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{appId}/{hash}.jpg",
            $"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{appId}/{hash}.jpg",
            $"https://media.steampowered.com/steamcommunity/public/images/apps/{appId}/{hash}.jpg"
        ];

        foreach (string url in urls)
        {
            if (await DownloadIfAvailableAsync(url, iconPath, cancellationToken))
                return true;
        }

        return false;
    }

    public static string? ResolveIconPath(string? coverImagePath)
    {
        if (string.IsNullOrWhiteSpace(coverImagePath))
            return null;

        string? directory = Path.GetDirectoryName(coverImagePath);
        return string.IsNullOrWhiteSpace(directory)
            ? null
            : Path.Combine(directory, "GameIcon.jpg");
    }

    private async Task<string?> GetCommunityIconHashAsync(string appId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.steamcmd.net/v1/info/{appId}");
        request.Headers.TryAddWithoutValidation("User-Agent", "EZManifest");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        var common = JsonNode.Parse(json)?["data"]?[appId]?["common"];
        return FirstNonEmpty(
            common?["icon"]?.ToString(),
            common?["logo"]?.ToString(),
            common?["logo_small"]?.ToString());
    }

    public async Task<bool> DownloadHeroAsync(
        string appId,
        string heroPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(heroPath))
            return false;

        if (File.Exists(heroPath) && new FileInfo(heroPath).Length > 0)
            return true;

        string[] urls =
        [
            $"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/library_hero_2x.jpg",
            $"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/library_hero.jpg",
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_hero_2x.jpg",
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_hero.jpg"
        ];

        foreach (string url in urls)
        {
            if (await DownloadIfAvailableAsync(url, heroPath, cancellationToken))
                return true;
        }

        return false;
    }

    public static string? ResolveHeroPath(string? coverImagePath)
    {
        if (string.IsNullOrWhiteSpace(coverImagePath))
            return null;

        string? directory = Path.GetDirectoryName(coverImagePath);
        return string.IsNullOrWhiteSpace(directory)
            ? null
            : Path.Combine(directory, "LibraryHero.jpg");
    }

    private async Task<bool> DownloadIfAvailableAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return false;

        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(output, cancellationToken);
        return File.Exists(destination) && new FileInfo(destination).Length > 0;
    }
}
