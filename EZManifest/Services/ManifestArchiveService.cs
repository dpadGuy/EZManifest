using System.IO.Compression;
using EZManifest.Models;

namespace EZManifest.Services;

public sealed class ManifestArchiveService
{
    public async Task<ManifestArchiveResult> ExtractAsync(string zipPath)
    {
        string archiveName = Path.GetFileNameWithoutExtension(zipPath);
        string manifestsRoot = AppPaths.ManifestsDirectory;
        string extractionDirectory = Path.Combine(manifestsRoot, archiveName);

        if (Directory.Exists(extractionDirectory))
        {
            try
            {
                await Task.Run(() => Directory.Delete(extractionDirectory, recursive: true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                extractionDirectory = Path.Combine(
                    manifestsRoot,
                    $"{archiveName}_{DateTime.UtcNow:yyyyMMddHHmmssfff}");
            }
        }

        Directory.CreateDirectory(extractionDirectory);
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractionDirectory));

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
