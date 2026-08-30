using EZManifest.Models;

namespace EZManifest.Services;

/// <summary>
/// Preselects depots Steam would install on this Windows PC.
/// macOS-only and Linux-only depots stay unchecked.
/// </summary>
internal static class SteamDepotPlatformFilter
{
    private const string DefaultLanguage = "english";

    public static HashSet<string> SelectWindowsDepotIds(
        IReadOnlyList<(DepotInfo Depot, DepotDisplayInfo Display, DepotMetadata? Meta)> rows)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);

        var windows = rows
            .Where(row =>
                row.Display.HasLocalManifest
                && !IsMacOsOrLinuxOnly(row.Meta, row.Display)
                && MatchesWindows(row.Meta, row.Display)
                && MatchesHostArch(row.Meta)
                && !IsOptional(row.Meta)
                && row.Meta?.IsShared != true)
            .ToList();

        var languageMatched = windows
            .Where(row => MatchesDefaultLanguage(row.Meta))
            .ToList();

        var auto = languageMatched.Count > 0 ? languageMatched : windows;
        foreach (var row in auto)
            selected.Add(row.Depot.DepotId);

        return selected;
    }

    public static int ListRank(
        DepotMetadata? meta,
        DepotDisplayInfo display)
    {
        return IsMacOsOrLinuxOnly(meta, display) ? 1 : 0;
    }

    public static bool IsMacOsOrLinuxOnly(DepotMetadata? meta) =>
        IsMacOsOrLinuxOnly(meta, new DepotDisplayInfo
        {
            DepotName = meta?.Name ?? string.Empty,
            Configuration = meta?.Configuration ?? string.Empty
        });

    public static bool IsMacOsOrLinuxOnly(DepotMetadata? meta, DepotDisplayInfo display)
    {
        string? os = EffectiveOsList(meta, display);
        if (string.IsNullOrWhiteSpace(os))
            return false;

        var tokens = SplitOs(os);
        bool hasWindows = tokens.Any(token => token.Equals("windows", StringComparison.OrdinalIgnoreCase));
        bool hasMac = tokens.Any(token =>
            token.Equals("macos", StringComparison.OrdinalIgnoreCase)
            || token.Equals("osx", StringComparison.OrdinalIgnoreCase)
            || token.Equals("mac", StringComparison.OrdinalIgnoreCase));
        bool hasLinux = tokens.Any(token => token.Equals("linux", StringComparison.OrdinalIgnoreCase));
        return !hasWindows && (hasMac || hasLinux);
    }

    private static bool MatchesWindows(DepotMetadata? meta, DepotDisplayInfo display)
    {
        string? os = EffectiveOsList(meta, display);
        if (string.IsNullOrWhiteSpace(os))
            return true;

        return SplitOs(os).Any(token => token.Equals("windows", StringComparison.OrdinalIgnoreCase));
    }

    private static string? EffectiveOsList(DepotMetadata? meta, DepotDisplayInfo display)
    {
        if (!string.IsNullOrWhiteSpace(meta?.OsList))
            return meta.OsList;

        return InferOsFromName(meta?.Name) ?? InferOsFromName(display.DepotName) ?? InferOsFromName(display.Configuration);
    }

    private static string? InferOsFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (name.Contains("macos", StringComparison.OrdinalIgnoreCase)
            || name.Contains("osx", StringComparison.OrdinalIgnoreCase)
            || name.Contains("mac os", StringComparison.OrdinalIgnoreCase))
            return "macos";

        if (name.Contains("linux", StringComparison.OrdinalIgnoreCase))
            return "linux";

        if (name.Contains("windows", StringComparison.OrdinalIgnoreCase)
            || name.Contains("win32", StringComparison.OrdinalIgnoreCase)
            || name.Contains("win64", StringComparison.OrdinalIgnoreCase))
            return "windows";

        return null;
    }

    private static IEnumerable<string> SplitOs(string os) =>
        os.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool MatchesHostArch(DepotMetadata? meta)
    {
        if (string.IsNullOrWhiteSpace(meta?.OsArch))
            return true;

        string host = Environment.Is64BitOperatingSystem ? "64" : "32";
        return meta.OsArch.Equals(host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesDefaultLanguage(DepotMetadata? meta)
    {
        if (string.IsNullOrWhiteSpace(meta?.Language))
            return true;

        string language = meta.Language.Trim().ToLowerInvariant().Replace('-', '_');
        return language is DefaultLanguage or "en" or "en_us" or "en_gb";
    }

    private static bool IsOptional(DepotMetadata? meta) => meta?.IsOptional == true;
}
