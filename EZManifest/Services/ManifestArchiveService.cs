using System.IO.Compression;
using EZManifest.Models;

namespace EZManifest.Services;

public sealed class ManifestArchiveService
{
    private static readonly SemaphoreSlim ExtractGate = new(1, 1);

    public async Task<ManifestArchiveResult> ExtractAsync(string zipPath)
    {
        await ExtractGate.WaitAsync();
        try
        {
            return await ExtractCoreAsync(zipPath);
        }
        finally
        {
            ExtractGate.Release();
        }
    }

    /// <summary>Finds an extracted manifest folder that contains <c>{appId}.lua</c>.</summary>
    public static string? FindExtractionDirectory(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !Directory.Exists(AppPaths.ManifestsDirectory))
            return null;

        string luaName = $"{appId}.lua";
        foreach (string dir in Directory.EnumerateDirectories(AppPaths.ManifestsDirectory))
        {
            if (File.Exists(Path.Combine(dir, luaName)))
                return dir;
        }

        return null;
    }

    public static string? FindLuaPath(string appId)
    {
        string? dir = FindExtractionDirectory(appId);
        if (dir is null)
            return null;

        string luaPath = Path.Combine(dir, $"{appId}.lua");
        return File.Exists(luaPath) ? luaPath : null;
    }

    private static async Task<ManifestArchiveResult> ExtractCoreAsync(string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Manifest archive was not found.", zipPath);

        string archiveName = Path.GetFileNameWithoutExtension(zipPath);
        string manifestsRoot = AppPaths.ManifestsDirectory;
        Directory.CreateDirectory(manifestsRoot);

        string extractionDirectory = Path.Combine(manifestsRoot, archiveName);
        if (Directory.Exists(extractionDirectory))
        {
            try
            {
                await Task.Run(() => Directory.Delete(extractionDirectory, recursive: true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Write($"[ManifestArchive] Could not replace '{extractionDirectory}': {ex.Message}");
                extractionDirectory = Path.Combine(
                    manifestsRoot,
                    $"{archiveName}_{DateTime.UtcNow:yyyyMMddHHmmssfff}");
            }
        }

        Directory.CreateDirectory(extractionDirectory);

        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractionDirectory));
        }
        catch
        {
            try
            {
                if (Directory.Exists(extractionDirectory))
                    Directory.Delete(extractionDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup after a failed extract.
            }

            throw;
        }

        string? luaFile = Directory.GetFiles(extractionDirectory, "*.lua").FirstOrDefault();
        if (luaFile is null)
            throw new InvalidDataException("No .lua file found in the manifest archive.");

        string assetsDirectory = Path.Combine(extractionDirectory, "Assets");
        Directory.CreateDirectory(assetsDirectory);

        return new ManifestArchiveResult
        {
            ExtractionDirectory = extractionDirectory,
            LuaFilePath = luaFile,
            AppId = Path.GetFileNameWithoutExtension(luaFile),
            LogoPath = Path.Combine(assetsDirectory, "GameLogo.png"),
            CoverArtPath = Path.Combine(assetsDirectory, "VerticalCoverArt.jpg")
        };
    }
}
