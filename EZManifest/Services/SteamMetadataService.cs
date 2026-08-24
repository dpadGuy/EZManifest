using System.Net.Http;
using System.Text.Json.Nodes;

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

    public async Task DownloadArtworkAsync(
        string appId,
        string logoPath,
        string coverArtPath,
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
    }

    private async Task DownloadIfAvailableAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(output, cancellationToken);
    }
}
