namespace EZManifest.Services;

/// <summary>
/// Resolves writable paths next to the real executable.
/// AppContext.BaseDirectory points at a temp extract folder for single-file publishes.
/// </summary>
public static class AppPaths
{
    public static string ExeDirectory
    {
        get
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(processPath));
                if (!string.IsNullOrWhiteSpace(dir))
                    return dir;
            }

            return Path.GetFullPath(AppContext.BaseDirectory);
        }
    }

    public static string SettingsJson => Path.Combine(ExeDirectory, "settings.json");

    public static string ItemsJson => Path.Combine(ExeDirectory, "items.json");

    public static string ManifestsDirectory => Path.Combine(ExeDirectory, "Manifests");
}
