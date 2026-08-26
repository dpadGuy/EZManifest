using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using EZManifest.Models;

namespace EZManifest.Services;

public sealed class SteamDepotMetadataService
{
    private readonly HttpClient _httpClient;

    public SteamDepotMetadataService(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<IReadOnlyDictionary<string, DepotMetadata>> GetDepotMetadataAsync(
        string appId,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, DepotMetadata>(StringComparer.Ordinal);

        try
        {
            using var response = await _httpClient.GetAsync(
                $"https://api.steamcmd.net/v1/info/{appId}",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Write($"[DepotMetadata] HTTP {(int)response.StatusCode} for appId={appId}");
                return result;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty(appId, out var app) ||
                !app.TryGetProperty("depots", out var depots))
            {
                AppLog.Write($"[DepotMetadata] Unexpected JSON shape for appId={appId}");
                return result;
            }

            foreach (var depot in depots.EnumerateObject())
            {
                if (!ulong.TryParse(depot.Name, out _))
                    continue;

                string configuration = BuildConfiguration(depot.Value);
                long? size = null;
                long? download = null;

                if (depot.Value.TryGetProperty("manifests", out var manifests) &&
                    manifests.TryGetProperty("public", out var publicManifest))
                {
                    size = ReadLong(publicManifest, "size");
                    download = ReadLong(publicManifest, "download");
                }

                // Some dumps still expose legacy keys on the depot object itself.
                size ??= ReadLong(depot.Value, "maxsize");
                download ??= ReadLong(depot.Value, "dltotalsize");

                if (depot.Value.TryGetProperty("name", out var nameNode))
                {
                    string? name = nameNode.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        configuration = string.IsNullOrWhiteSpace(configuration)
                            ? name
                            : $"{configuration}  {name}";
                }

                result[depot.Name] = new DepotMetadata
                {
                    DepotId = depot.Name,
                    Configuration = configuration,
                    SizeBytes = size,
                    DownloadBytes = download
                };
            }

            AppLog.Write($"[DepotMetadata] Loaded {result.Count} depot(s) for appId={appId}");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"[DepotMetadata] Failed for appId={appId}");
        }

        return result;
    }

    private static string BuildConfiguration(JsonElement depot)
    {
        if (!depot.TryGetProperty("config", out var config))
            return string.Empty;

        var parts = new List<string>();
        string? oslist = config.TryGetProperty("oslist", out var osNode) ? osNode.GetString() : null;
        string? osarch = config.TryGetProperty("osarch", out var archNode) ? archNode.GetString() : null;

        if (!string.IsNullOrWhiteSpace(oslist))
        {
            foreach (string os in oslist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                parts.Add(os switch
                {
                    "windows" => "Windows",
                    "macos" => "macOS",
                    "linux" => "Linux",
                    _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(os)
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(osarch))
            parts.Add(osarch == "64" ? "64-bit" : osarch == "32" ? "32-bit" : osarch);

        return string.Join("  ", parts);
    }

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
            return number;

        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }
}

public sealed class DepotMetadata
{
    public string DepotId { get; init; } = string.Empty;
    public string Configuration { get; init; } = string.Empty;
    public long? SizeBytes { get; init; }
    public long? DownloadBytes { get; init; }
}
