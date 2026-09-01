namespace EZManifest.Services;

/// <summary>
/// User data lives in LocalAppData. Bundled files stay next to the executable.
/// AppContext.BaseDirectory points at a temp extract folder for single-file publishes.
/// </summary>
public static class AppPaths
{
    static AppPaths()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ManifestsDirectory);
        TryMigrateFromExeDirectory();
    }

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

    public static string DataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EZManifest");

    public static string SettingsJson => Path.Combine(DataDirectory, "settings.json");

    public static string ItemsJson => Path.Combine(DataDirectory, "items.json");

    public static string ManifestsDirectory => Path.Combine(DataDirectory, "Manifests");

    public static string RelocateStoredPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path ?? string.Empty;

        string oldManifests = Path.Combine(ExeDirectory, "Manifests");
        if (!path.StartsWith(oldManifests, StringComparison.OrdinalIgnoreCase))
            return path;

        string relative = Path.GetRelativePath(oldManifests, path);
        string relocated = Path.Combine(ManifestsDirectory, relative);
        return File.Exists(relocated) || Directory.Exists(relocated) ? relocated : path;
    }

    private static void TryMigrateFromExeDirectory()
    {
        try
        {
            RelocateFile(Path.Combine(ExeDirectory, "settings.json"), SettingsJson);
            RelocateFile(Path.Combine(ExeDirectory, "items.json"), ItemsJson);
            RelocateDirectory(Path.Combine(ExeDirectory, "Manifests"), ManifestsDirectory);
        }
        catch (Exception ex)
        {
            AppLog.Write($"[AppPaths] Migration from exe folder failed: {ex.Message}");
        }
    }

    private static void RelocateFile(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            File.Move(source, destination);
        }
        catch (IOException)
        {
            File.Copy(source, destination, overwrite: false);
        }

        AppLog.Write($"[AppPaths] Moved '{source}' → '{destination}'");
    }

    private static void RelocateDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
            return;

        bool destHasContent = Directory.Exists(destination)
            && Directory.EnumerateFileSystemEntries(destination).Any();
        if (destHasContent)
            return;

        if (Directory.Exists(destination))
            Directory.Delete(destination);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException)
        {
            CopyDirectory(source, destination);
        }

        AppLog.Write($"[AppPaths] Moved '{source}' → '{destination}'");
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (string file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);

        foreach (string subDir in Directory.GetDirectories(sourceDir))
            CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
    }
}
