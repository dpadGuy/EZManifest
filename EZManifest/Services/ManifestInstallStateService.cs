using EZManifest.Models;

namespace EZManifest.Services;

public static class ManifestInstallStateService
{
    public static string StateDirectory(string appId) =>
        Path.Combine(AppPaths.DataDirectory, "InstallState", appId);

    public static void SaveSnapshots(string appId, IEnumerable<DepotInfo> depots)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return;

        string dir = StateDirectory(appId);
        Directory.CreateDirectory(dir);

        foreach (string leftover in Directory.EnumerateFiles(dir, "*.manifest"))
        {
            try
            {
                File.Delete(leftover);
            }
            catch (Exception ex)
            {
                AppLog.Write($"[InstallState] Could not replace '{leftover}': {ex.Message}");
            }
        }

        foreach (DepotInfo depot in depots)
        {
            if (string.IsNullOrWhiteSpace(depot.ManifestPath) || !File.Exists(depot.ManifestPath))
                continue;

            string dest = Path.Combine(dir, $"{depot.DepotId}_{depot.ManifestId}.manifest");
            File.Copy(depot.ManifestPath, dest, overwrite: true);
        }
    }

    public static IReadOnlyList<DepotInfo> LoadSnapshots(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return [];

        string dir = StateDirectory(appId);
        if (!Directory.Exists(dir))
            return [];

        var depots = new List<DepotInfo>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.manifest"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            int split = name.IndexOf('_');
            if (split <= 0 || split >= name.Length - 1)
                continue;

            depots.Add(new DepotInfo
            {
                DepotId = name[..split],
                ManifestId = name[(split + 1)..],
                ManifestPath = file
            });
        }

        return depots;
    }

    public static Dictionary<string, string> SnapshotPathsByDepot(string appId) =>
        LoadSnapshots(appId)
            .Where(depot => !string.IsNullOrWhiteSpace(depot.DepotId) && File.Exists(depot.ManifestPath))
            .GroupBy(depot => depot.DepotId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ManifestPath, StringComparer.Ordinal);

    public static void DeleteSnapshots(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return;

        string dir = StateDirectory(appId);
        if (!Directory.Exists(dir))
            return;

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            AppLog.Write($"[InstallState] Could not remove '{dir}': {ex.Message}");
        }
    }
}
