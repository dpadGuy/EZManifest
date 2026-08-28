using System.IO.Compression;
using EZManifest.Models;

namespace EZManifest.Services;

public sealed class ManifestArchiveService
{
    private static readonly SemaphoreSlim ExtractGate = new(1, 1);

    /// <summary>Imports a .zip archive or a folder that contains a .lua manifest.</summary>
    public async Task<ManifestArchiveResult> ExtractAsync(string path)
    {
        await ExtractGate.WaitAsync();
        try
        {
            if (Directory.Exists(path))
                return await ImportFolderCoreAsync(path);

            if (File.Exists(path))
                return await ExtractZipCoreAsync(path);

            throw new FileNotFoundException("Manifest archive or folder was not found.", path);
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

    private static async Task<ManifestArchiveResult> ExtractZipCoreAsync(string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Manifest archive was not found.", zipPath);

        string archiveName = Path.GetFileNameWithoutExtension(zipPath);
        string extractionDirectory = await PrepareExtractionDirectoryAsync(archiveName);

        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractionDirectory));
        }
        catch
        {
            TryDeleteDirectory(extractionDirectory);
            throw;
        }

        return BuildResult(extractionDirectory, "No .lua file found in the manifest archive.");
    }

    private static async Task<ManifestArchiveResult> ImportFolderCoreAsync(string sourceFolder)
    {
        sourceFolder = Path.GetFullPath(sourceFolder);
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException($"Manifest folder was not found: {sourceFolder}");

        string? luaSource = FindLuaInFolder(sourceFolder)
            ?? throw new InvalidDataException(
                "No .lua file found in the folder (checked the folder and one level of subfolders).");

        // Copy the directory that actually holds the .lua so manifests sit at the extract root.
        string contentRoot = Path.GetDirectoryName(luaSource)
            ?? throw new InvalidDataException("Could not resolve the manifest folder.");

        string folderName = Path.GetFileName(
            sourceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName))
            folderName = Path.GetFileNameWithoutExtension(luaSource);

        string extractionDirectory = await PrepareExtractionDirectoryAsync(folderName);

        try
        {
            await Task.Run(() => CopyDirectory(contentRoot, extractionDirectory));
        }
        catch
        {
            TryDeleteDirectory(extractionDirectory);
            throw;
        }

        return BuildResult(extractionDirectory, "No .lua file found after importing the folder.");
    }

    private static async Task<string> PrepareExtractionDirectoryAsync(string archiveName)
    {
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
        return extractionDirectory;
    }

    private static ManifestArchiveResult BuildResult(string extractionDirectory, string missingLuaMessage)
    {
        string? luaFile = Directory.GetFiles(extractionDirectory, "*.lua").FirstOrDefault();
        if (luaFile is null)
            throw new InvalidDataException(missingLuaMessage);

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

    private static string? FindLuaInFolder(string root)
    {
        try
        {
            string[] atRoot = Directory.GetFiles(root, "*.lua");
            if (atRoot.Length > 0)
                return atRoot[0];

            foreach (string sub in Directory.GetDirectories(root))
            {
                string[] nested = Directory.GetFiles(sub, "*.lua");
                if (nested.Length > 0)
                    return nested[0];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"[ManifestArchive] Failed scanning folder '{root}': {ex.Message}");
        }

        return null;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string destSub = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSub);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup after a failed import.
        }
    }
}
