using EZManifest.Models;

namespace EZManifest.Services;

/// <summary>
/// Preselects depots Steam would install on this Windows PC.
/// macOS-only and Linux-only depots stay unchecked.
/// </summary>
internal static class SteamDepotPlatformFilter
{
    public static HashSet<string> SelectWindowsDepotIds(
        IReadOnlyList<(DepotInfo Depot, DepotDisplayInfo Display, DepotMetadata? Meta)> rows)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);

        var candidates = rows
            .Where(row =>
                row.Display.HasLocalManifest
                && !row.Display.IsLanguage
                && !row.Display.IsDlc
                && !row.Display.IsShared
                && !IsOptional(row.Meta)
                && !IsMacOsOrLinuxOnly(row.Meta, row.Display)
                && MatchesWindows(row.Meta, row.Display))
            .ToList();

        candidates = PreferHostArch(candidates, row => row.Display.OsArch ?? row.Meta?.OsArch);

        bool hasSteamMetadata = rows.Any(row => row.Meta is not null);

        if (candidates.Count == 0)
            return selected;

        // Empty oslist is Steam's default Windows depot. Only refuse a mass-check
        // when we have no metadata at all and more than one leftover row.
        if (!hasSteamMetadata && candidates.Count != 1)
            return selected;

        foreach (var row in candidates)
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

    public static List<(DepotInfo Depot, DepotDisplayInfo Display)> PreferHostArch(
        IReadOnlyList<(DepotInfo Depot, DepotDisplayInfo Display)> rows) =>
        PreferHostArch(rows, row => row.Display.OsArch);

    public static List<T> PreferHostArch<T>(IReadOnlyList<T> rows, Func<T, string?> osArch)
    {
        string preferred = Environment.Is64BitOperatingSystem ? "64" : "32";
        if (!rows.Any(row => osArch(row).EqualsArch(preferred)))
            return rows.ToList();

        return rows
            .Where(row =>
            {
                string? arch = osArch(row);
                return string.IsNullOrWhiteSpace(arch) || arch.EqualsArch(preferred);
            })
            .ToList();
    }

    private static bool EqualsArch(this string? value, string arch) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Equals(arch, StringComparison.OrdinalIgnoreCase);

    private static bool IsOptional(DepotMetadata? meta) => meta?.IsOptional == true;
}
