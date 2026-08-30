using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using EZManifest.Models;
using SteamKit2;

namespace EZManifest.Services;

public sealed class SteamDepotMetadataService
{
    private static readonly HttpClient SteamCmdClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(90)
    };

    public SteamDepotMetadataService(HttpClient _)
    {
    }

    public async Task<IReadOnlyDictionary<string, DepotMetadata>> GetDepotMetadataAsync(
        string appId,
        IEnumerable<string>? knownDepotIds = null,
        IEnumerable<string>? relatedAppIds = null,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, DepotMetadata>(StringComparer.OrdinalIgnoreCase);
        var fetchedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            AppLog.Write($"[DepotMetadata] Resolving OS for appId={appId}");
            await MergeFromSteamPicsAsync(appId, knownDepotIds, relatedAppIds, result, cancellationToken);

            if (!HasOsInfo(result, knownDepotIds))
            {
                AppLog.Write($"[DepotMetadata] Steam PICS missed OS info for appId={appId}; trying steamcmd");
                await MergeAppDepotsAsync(appId, result, fetchedApps, dlcOverride: false, cancellationToken);
                var related = CollectRelatedAppIds(result, knownDepotIds, relatedAppIds);
                foreach (string relatedAppId in related)
                    await MergeAppDepotsAsync(relatedAppId, result, fetchedApps, dlcOverride: true, cancellationToken);
            }

            AppLog.Write(
                $"[DepotMetadata] Loaded {result.Count} depot(s) for appId={appId} " +
                $"with OS: {result.Values.Count(meta => !string.IsNullOrWhiteSpace(meta.OsList))}");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"[DepotMetadata] Failed for appId={appId}");
        }

        return result;
    }

    private static HashSet<string> CollectRelatedAppIds(
        Dictionary<string, DepotMetadata> result,
        IEnumerable<string>? knownDepotIds,
        IEnumerable<string>? relatedAppIds)
    {
        var related = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in result.Values)
        {
            if (!string.IsNullOrWhiteSpace(meta.DlcAppId))
                related.Add(meta.DlcAppId);
        }

        if (relatedAppIds is not null)
        {
            foreach (string id in relatedAppIds)
                related.Add(id);
        }

        if (knownDepotIds is not null)
        {
            foreach (string depotId in knownDepotIds)
            {
                if (result.ContainsKey(depotId))
                    continue;

                related.Add(depotId);
                string? sibling = RelatedAppId(depotId);
                if (sibling is not null)
                    related.Add(sibling);
            }
        }

        return related;
    }

    private static bool HasOsInfo(
        Dictionary<string, DepotMetadata> result,
        IEnumerable<string>? knownDepotIds)
    {
        if (knownDepotIds is not null)
        {
            foreach (string depotId in knownDepotIds)
            {
                if (result.TryGetValue(depotId, out var meta) && !string.IsNullOrWhiteSpace(meta.OsList))
                    return true;
            }

            return false;
        }

        return result.Values.Any(meta => !string.IsNullOrWhiteSpace(meta.OsList));
    }

    private async Task MergeAppDepotsAsync(
        string appId,
        Dictionary<string, DepotMetadata> result,
        HashSet<string> fetchedApps,
        bool dlcOverride,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appId) || !fetchedApps.Add(appId))
            return;

        using var document = await FetchAppInfoAsync(appId, cancellationToken);
        if (document is null
            || !document.RootElement.TryGetProperty("data", out var data)
            || !TryGetApp(data, appId, out var app)
            || !app.TryGetProperty("depots", out var depots))
        {
            return;
        }

        bool appIsDlc = dlcOverride || AppLooksLikeDlc(app);
        var dlcAppIds = ReadDlcAppIds(app, depots);
        string? appName = app.TryGetProperty("common", out var common)
            ? ReadString(common, "name")
            : null;

        foreach (var depot in depots.EnumerateObject())
        {
            if (!ulong.TryParse(depot.Name, out _))
                continue;

            var platform = ReadPlatformConfig(depot.Value);
            string? name = depot.Value.TryGetProperty("name", out var nameNode)
                ? nameNode.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name))
                name = appName;

            string? inferredOs = InferOsList(platform.OsList, name);
            var platformWithOs = platform with { OsList = inferredOs };
            string configuration = BuildConfiguration(platformWithOs, name);

            long? size = null;
            long? download = null;
            if (depot.Value.TryGetProperty("manifests", out var manifests) &&
                manifests.TryGetProperty("public", out var publicManifest))
            {
                size = ReadLong(publicManifest, "size");
                download = ReadLong(publicManifest, "download");
            }

            size ??= ReadLong(depot.Value, "maxsize");
            download ??= ReadLong(depot.Value, "dltotalsize");

            string? dlcAppId = ReadString(depot.Value, "dlcappid");
            if (string.IsNullOrWhiteSpace(dlcAppId) && (appIsDlc || dlcAppIds.Contains(depot.Name)))
                dlcAppId = appIsDlc ? appId : depot.Name;

            bool isShared = IsTruthy(depot.Value, "sharedinstall")
                || !string.IsNullOrWhiteSpace(ReadString(depot.Value, "depotfromapp"));
            bool isDlc = !isShared && (
                appIsDlc
                || !string.IsNullOrWhiteSpace(dlcAppId)
                || dlcAppIds.Contains(depot.Name)
                || NameLooksLikeDlc(name));

            // Parent appinfo wins for the same depot id; DLC stubs without manifests should not replace it.
            if (result.TryGetValue(depot.Name, out var existing)
                && existing.HasManifests
                && size is null
                && download is null)
            {
                continue;
            }

            result[depot.Name] = new DepotMetadata
            {
                DepotId = depot.Name,
                Name = name ?? string.Empty,
                Configuration = configuration,
                OsList = inferredOs,
                OsArch = platform.OsArch,
                Language = platform.Language,
                IsOptional = platform.IsOptional,
                IsDlc = isDlc,
                IsShared = isShared,
                DlcAppId = dlcAppId,
                HasManifests = size is not null || download is not null
                    || (depot.Value.TryGetProperty("manifests", out var m) && m.ValueKind == JsonValueKind.Object),
                SizeBytes = size,
                DownloadBytes = download
            };
        }

        if (app.TryGetProperty("extended", out var extended))
        {
            string? list = ReadString(extended, "listofdlc");
            if (!string.IsNullOrWhiteSpace(list))
            {
                foreach (string id in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    dlcAppIds.Add(id);
            }
        }

        foreach (string dlcAppId in dlcAppIds)
            await MergeAppDepotsAsync(dlcAppId, result, fetchedApps, dlcOverride: true, cancellationToken);
    }

    private async Task MergeFromSteamPicsAsync(
        string appId,
        IEnumerable<string>? knownDepotIds,
        IEnumerable<string>? relatedAppIds,
        Dictionary<string, DepotMetadata> result,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<uint>();
        if (uint.TryParse(appId, out uint parentId))
            ids.Add(parentId);

        foreach (string related in CollectRelatedAppIds(result, knownDepotIds, relatedAppIds))
        {
            if (uint.TryParse(related, out uint relatedId))
                ids.Add(relatedId);
        }

        IReadOnlyDictionary<uint, KeyValue> infos;
        try
        {
            infos = await SteamService.GetAppInfosAsync(ids, cancellationToken);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"[DepotMetadata] Steam PICS failed for appId={appId}");
            return;
        }

        foreach (var (id, keyValues) in infos)
            MergeKeyValueApp(id.ToString(CultureInfo.InvariantCulture), keyValues, result, dlcOverride: id.ToString(CultureInfo.InvariantCulture) != appId);

        var extra = new HashSet<uint>();
        foreach (var meta in result.Values)
        {
            if (uint.TryParse(meta.DlcAppId, out uint dlcId) && !infos.ContainsKey(dlcId))
                extra.Add(dlcId);
        }

        if (extra.Count == 0)
            return;

        try
        {
            infos = await SteamService.GetAppInfosAsync(extra, cancellationToken);
            foreach (var (id, keyValues) in infos)
                MergeKeyValueApp(id.ToString(CultureInfo.InvariantCulture), keyValues, result, dlcOverride: true);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "[DepotMetadata] Steam PICS extra DLC fetch failed");
        }
    }

    private static void MergeKeyValueApp(
        string appId,
        KeyValue app,
        Dictionary<string, DepotMetadata> result,
        bool dlcOverride)
    {
        KeyValue depots = app["depots"];
        if (depots.Children.Count == 0)
            return;

        bool appIsDlc = dlcOverride || AppLooksLikeDlc(app);
        var dlcAppIds = ReadDlcAppIds(app, depots);
        string? appName = KvValue(app["common"]["name"]);

        foreach (var depot in depots.Children)
        {
            if (!ulong.TryParse(depot.Name, out _))
                continue;

            string? osList = KvValue(depot["config"]["oslist"]);
            string? osArch = KvValue(depot["config"]["osarch"]);
            string? language = KvValue(depot["config"]["language"]);
            bool isOptional = IsTruthy(depot, "optional") || IsTruthy(depot["config"], "optional");
            string? name = KvValue(depot["name"]) ?? appName;
            string? inferredOs = InferOsList(osList, name);
            string configuration = BuildConfiguration(
                new DepotPlatformConfig(inferredOs, osArch, language, isOptional),
                name);

            long? size = KvLong(depot["manifests"]["public"]["size"]) ?? KvLong(depot["maxsize"]);
            long? download = KvLong(depot["manifests"]["public"]["download"]) ?? KvLong(depot["dltotalsize"]);

            string? dlcAppId = KvValue(depot["dlcappid"]);
            if (string.IsNullOrWhiteSpace(dlcAppId) && (appIsDlc || dlcAppIds.Contains(depot.Name)))
                dlcAppId = appIsDlc ? appId : depot.Name;

            bool isShared = IsTruthy(depot, "sharedinstall") || !string.IsNullOrWhiteSpace(KvValue(depot["depotfromapp"]));
            bool isDlc = !isShared && (
                appIsDlc
                || !string.IsNullOrWhiteSpace(dlcAppId)
                || dlcAppIds.Contains(depot.Name)
                || NameLooksLikeDlc(name));

            if (result.TryGetValue(depot.Name, out var existing)
                && existing.HasManifests
                && size is null
                && download is null
                && !string.IsNullOrWhiteSpace(existing.OsList))
            {
                continue;
            }

            result[depot.Name] = new DepotMetadata
            {
                DepotId = depot.Name,
                Name = name ?? string.Empty,
                Configuration = configuration,
                OsList = inferredOs,
                OsArch = osArch,
                Language = language,
                IsOptional = isOptional,
                IsDlc = isDlc,
                IsShared = isShared,
                DlcAppId = dlcAppId,
                HasManifests = size is not null || download is not null || depot["manifests"].Children.Count > 0,
                SizeBytes = size ?? existing?.SizeBytes,
                DownloadBytes = download ?? existing?.DownloadBytes
            };
        }
    }

    private static bool AppLooksLikeDlc(KeyValue app)
    {
        string? type = KvValue(app["common"]["type"]);
        return type is not null && (
            type.Equals("DLC", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Music", StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> ReadDlcAppIds(KeyValue app, KeyValue depots)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? list = KvValue(app["extended"]["listofdlc"]);
        if (!string.IsNullOrWhiteSpace(list))
        {
            foreach (string id in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                ids.Add(id);
        }

        foreach (var depot in depots.Children)
        {
            string? dlcAppId = KvValue(depot["dlcappid"]);
            if (!string.IsNullOrWhiteSpace(dlcAppId))
                ids.Add(dlcAppId);
        }

        return ids;
    }

    private static string? KvValue(KeyValue node) =>
        string.IsNullOrWhiteSpace(node.Value) ? null : node.Value;

    private static long? KvLong(KeyValue node)
    {
        string? text = KvValue(node);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value
            : null;
    }

    private static bool IsTruthy(KeyValue node, string propertyName)
    {
        string? value = KvValue(node[propertyName]);
        return value is "1" or "true" or "True";
    }

    private async Task<JsonDocument?> FetchAppInfoAsync(string appId, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://api.steamcmd.net/v1/info/{appId}");
                request.Headers.TryAddWithoutValidation("User-Agent", "EZManifest");

                using var response = await SteamCmdClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Write($"[DepotMetadata] HTTP {(int)response.StatusCode} for appId={appId} (try {attempt})");
                    await Task.Delay(500 * attempt, cancellationToken);
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (HasAppData(document, appId))
                    return document;

                AppLog.Write($"[DepotMetadata] Empty steamcmd payload for appId={appId} (try {attempt})");
                document.Dispose();
                await Task.Delay(500 * attempt, cancellationToken);
            }
            catch (Exception ex) when (attempt < 3)
            {
                lastError = ex;
                AppLog.Write($"[DepotMetadata] Retry {attempt} for appId={appId}: {ex.Message}");
                await Task.Delay(500 * attempt, cancellationToken);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
            AppLog.Write(lastError, $"[DepotMetadata] Failed fetching appId={appId}");
        return null;
    }

    private static bool HasAppData(JsonDocument document, string appId)
    {
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !TryGetApp(data, appId, out var app))
        {
            return false;
        }

        return app.ValueKind == JsonValueKind.Object
            && app.TryGetProperty("depots", out var depots)
            && depots.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetApp(JsonElement data, string appId, out JsonElement app)
    {
        if (data.TryGetProperty(appId, out app))
            return true;

        foreach (var property in data.EnumerateObject())
        {
            if (property.Name.Equals(appId, StringComparison.OrdinalIgnoreCase))
            {
                app = property.Value;
                return true;
            }
        }

        app = default;
        return false;
    }

    private static HashSet<string> ReadDlcAppIds(JsonElement app, JsonElement depots)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (app.TryGetProperty("extended", out var extended))
        {
            string? list = ReadString(extended, "listofdlc");
            if (!string.IsNullOrWhiteSpace(list))
            {
                foreach (string id in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    ids.Add(id);
            }
        }

        if (app.TryGetProperty("dlc", out var dlc) && dlc.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in dlc.EnumerateObject())
            {
                if (ulong.TryParse(item.Name, out _))
                    ids.Add(item.Name);
            }
        }

        foreach (var depot in depots.EnumerateObject())
        {
            string? dlcAppId = ReadString(depot.Value, "dlcappid");
            if (!string.IsNullOrWhiteSpace(dlcAppId))
                ids.Add(dlcAppId);
        }

        return ids;
    }

    private static bool AppLooksLikeDlc(JsonElement app)
    {
        if (!app.TryGetProperty("common", out var common))
            return false;

        string? type = ReadString(common, "type");
        return type is not null && (
            type.Equals("DLC", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Music", StringComparison.OrdinalIgnoreCase));
    }

    private static string? RelatedAppId(string depotId)
    {
        if (!ulong.TryParse(depotId, out ulong id) || id == 0)
            return null;

        // Soundtrack / DLC packages are often appId = depotId with the last digit zeroed (704711 → 704710).
        ulong related = (id / 10) * 10;
        return related == id ? null : related.ToString(CultureInfo.InvariantCulture);
    }

    private static bool NameLooksLikeDlc(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (name.Contains("DLC", StringComparison.OrdinalIgnoreCase)
         || name.Contains("soundtrack", StringComparison.OrdinalIgnoreCase));

    private static string? InferOsList(string? oslist, string? name)
    {
        if (!string.IsNullOrWhiteSpace(oslist))
            return oslist;

        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (name.Contains("macos", StringComparison.OrdinalIgnoreCase)
            || name.Contains("osx", StringComparison.OrdinalIgnoreCase)
            || name.Contains("macOS", StringComparison.Ordinal))
            return "macos";

        if (name.Contains("linux", StringComparison.OrdinalIgnoreCase))
            return "linux";

        if (name.Contains("windows", StringComparison.OrdinalIgnoreCase)
            || name.Contains("win32", StringComparison.OrdinalIgnoreCase)
            || name.Contains("win64", StringComparison.OrdinalIgnoreCase))
            return "windows";

        return null;
    }

    private static DepotPlatformConfig ReadPlatformConfig(JsonElement depot)
    {
        JsonElement config = default;
        bool hasConfig = depot.TryGetProperty("config", out config);

        return new DepotPlatformConfig(
            OsList: hasConfig ? ReadString(config, "oslist") : null,
            OsArch: hasConfig ? ReadString(config, "osarch") : null,
            Language: hasConfig ? ReadString(config, "language") : null,
            IsOptional: IsTruthy(depot, "optional") || (hasConfig && IsTruthy(config, "optional")));
    }

    private static string BuildConfiguration(DepotPlatformConfig platform, string? name)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(platform.OsList))
        {
            foreach (string os in platform.OsList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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

        if (!string.IsNullOrWhiteSpace(platform.OsArch))
            parts.Add(platform.OsArch == "64" ? "64-bit" : platform.OsArch == "32" ? "32-bit" : platform.OsArch);

        if (!string.IsNullOrWhiteSpace(name))
            parts.Add(name);

        return string.Join("  ", parts);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static bool IsTruthy(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt64(out long number) && number != 0,
            JsonValueKind.String => value.GetString() is "1" or "true" or "True",
            _ => false
        };
    }

    private readonly record struct DepotPlatformConfig(
        string? OsList,
        string? OsArch,
        string? Language,
        bool IsOptional);

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
    public string Name { get; init; } = string.Empty;
    public string Configuration { get; init; } = string.Empty;
    public string? OsList { get; init; }
    public string? OsArch { get; init; }
    public string? Language { get; init; }
    public bool IsOptional { get; init; }
    public bool IsDlc { get; init; }
    public bool IsShared { get; init; }
    public bool HasManifests { get; init; }
    public string? DlcAppId { get; init; }
    public long? SizeBytes { get; init; }
    public long? DownloadBytes { get; init; }

    public string TypeLabel =>
        IsShared ? "Shared" : IsDlc ? "DLC" : "Game";
}
