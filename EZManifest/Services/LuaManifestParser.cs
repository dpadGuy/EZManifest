using System.Text.RegularExpressions;
using EZManifest.Models;

namespace EZManifest.Services;

public sealed class LuaManifestParser
{
    private static readonly Regex KeyRegex = new(
        @"addappid\s*\(\s*(\d+)\s*,\s*\d+\s*,\s*""([a-fA-F0-9]+)""\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RelatedAppIdRegex = new(
        @"addappid\s*\(\s*(\d+)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ManifestFileNameRegex = new(
        @"^(?<depot>\d+)_(?<manifest>\d+)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Builds depot list from on-disk *.manifest files next to the lua.
    /// Keys still come from addappid(...) in the lua; setManifestid is ignored.
    /// </summary>
    public IReadOnlyList<DepotInfo> Parse(string luaFilePath)
    {
        if (!File.Exists(luaFilePath))
            return Array.Empty<DepotInfo>();

        string? directory = Path.GetDirectoryName(luaFilePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Array.Empty<DepotInfo>();

        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        string text = File.ReadAllText(luaFilePath);
        foreach (Match match in KeyRegex.Matches(text))
            keys[match.Groups[1].Value] = match.Groups[2].Value;

        var depots = new List<DepotInfo>();
        foreach (string manifestFile in Directory.EnumerateFiles(directory, "*.manifest", SearchOption.AllDirectories))
        {
            string name = Path.GetFileNameWithoutExtension(manifestFile);
            Match nameMatch = ManifestFileNameRegex.Match(name);
            if (!nameMatch.Success)
                continue;

            string depotId = nameMatch.Groups["depot"].Value;
            string manifestId = nameMatch.Groups["manifest"].Value;

            keys.TryGetValue(depotId, out string? hexKey);

            depots.Add(new DepotInfo
            {
                DepotId = depotId,
                ManifestId = manifestId,
                ManifestPath = manifestFile,
                HexKey = hexKey ?? string.Empty
            });
        }

        return depots
            .OrderBy(depot => depot.DepotId, StringComparer.Ordinal)
            .ThenBy(depot => depot.ManifestId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// DLC/music apps listed as addappid(id) with no key — used to fetch Steam appinfo, not comments.
    /// </summary>
    public IReadOnlyList<string> ParseRelatedAppIds(string luaFilePath)
    {
        if (!File.Exists(luaFilePath))
            return Array.Empty<string>();

        string text = File.ReadAllText(luaFilePath);
        return RelatedAppIdRegex.Matches(text)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
