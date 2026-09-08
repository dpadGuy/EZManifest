using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace EZManifest.Services;

public readonly record struct PatchApplyProgress(string Phase, int Done, int Total, string? Current);

public sealed class PatchApplyService
{
    public async Task<string> ExtractArchiveAsync(
        string archivePath,
        IProgress<PatchApplyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Downloaded archive was not found.", archivePath);

        string dest = Path.Combine(
            AppPaths.DataDirectory,
            "PatchExtract",
            Path.GetFileNameWithoutExtension(archivePath) + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);

        try
        {
            string ext = Path.GetExtension(archivePath);
            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() => ExtractZip(archivePath, dest, progress, cancellationToken), cancellationToken);
            }
            else if (ext.Equals(".7z", StringComparison.OrdinalIgnoreCase) ||
                     ext.Equals(".rar", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await Task.Run(
                        () => ExtractWithSharpCompress(archivePath, dest, progress, cancellationToken),
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Write(ex, "SharpCompress extract failed; trying 7-Zip");
                    progress?.Report(new PatchApplyProgress("Extracting", 0, 0, "Using 7-Zip…"));
                    await ExtractSevenZipAsync(archivePath, dest, cancellationToken);
                }
            }
            else
            {
                throw new InvalidOperationException("Game fix archives must be .zip, .7z, or .rar.");
            }
        }
        catch
        {
            TryDeleteDirectory(dest);
            throw;
        }

        return dest;
    }

    public static string? FindFilesFolder(string extractedRoot)
    {
        if (!Directory.Exists(extractedRoot))
            return null;

        string direct = Path.Combine(extractedRoot, "files");
        if (Directory.Exists(direct))
            return direct;

        foreach (string dir in Directory.EnumerateDirectories(extractedRoot, "files", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(dir).Equals("files", StringComparison.OrdinalIgnoreCase))
                return dir;
        }

        return null;
    }

    public async Task<int> CopyFilesOverAsync(
        string filesFolder,
        string gameFolder,
        IProgress<PatchApplyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        filesFolder = Path.GetFullPath(filesFolder);
        gameFolder = Path.GetFullPath(gameFolder);
        if (!Directory.Exists(filesFolder))
            throw new DirectoryNotFoundException("The archive has no files folder.");
        if (!Directory.Exists(gameFolder))
            throw new DirectoryNotFoundException($"Game folder was not found:\n{gameFolder}");

        int copied = 0;
        await Task.Run(() =>
        {
            var sources = Directory.EnumerateFiles(filesFolder, "*", SearchOption.AllDirectories).ToList();
            int total = sources.Count;
            progress?.Report(new PatchApplyProgress("Applying", 0, total, null));

            foreach (string source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(filesFolder, source);
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                    continue;

                string dest = Path.GetFullPath(Path.Combine(gameFolder, relative));
                if (!dest.StartsWith(gameFolder, StringComparison.OrdinalIgnoreCase))
                    continue;

                string? destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrWhiteSpace(destDir))
                    Directory.CreateDirectory(destDir);

                File.Copy(source, dest, overwrite: true);
                copied++;
                progress?.Report(new PatchApplyProgress("Applying", copied, total, relative));
            }
        }, cancellationToken);

        return copied;
    }

    public static void UnblockFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            NativeDeleteFile(path + ":Zone.Identifier");
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Patch] Could not unblock '{path}': {ex.Message}");
        }
    }

    public static string DownloadsFolder => Path.Combine(AppPaths.DataDirectory, "PatchDownloads");

    public static string ExtractFolder => Path.Combine(AppPaths.DataDirectory, "PatchExtract");

    public static void TryDeleteFile(string? path) =>
        DeleteFileCore(path, log: true);

    public static async Task TryDeleteFileAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        DeleteSidecars(path);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (DeleteFileCore(path, log: attempt == 0 || !File.Exists(path)))
                return;

            await Task.Delay(150 * (attempt + 1));
        }

        if (File.Exists(path))
            AppLog.Write($"[Patch] Could not remove archive '{path}' after retries");
    }

    public static async Task CleanupDownloadsAsync(string? keepPath = null)
    {
        string folder = DownloadsFolder;
        if (!Directory.Exists(folder))
            return;

        foreach (string file in Directory.EnumerateFiles(folder))
        {
            if (keepPath is not null &&
                string.Equals(file, keepPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await TryDeleteFileAsync(file);
        }
    }

    public static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                }
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Patch] Could not remove '{path}': {ex.Message}");
        }
    }

    private static bool DeleteFileCore(string? path, bool log)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return true;

        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            if (log)
                AppLog.Write($"[Patch] Removed archive '{path}'");
            return true;
        }
        catch (Exception ex)
        {
            if (log)
                AppLog.Write($"[Patch] Could not remove archive '{path}': {ex.Message}");
            return false;
        }
    }

    private static void DeleteSidecars(string path)
    {
        string[] extras =
        [
            path + ".crdownload",
            path + ".tmp",
            path + ".partial",
            path + ".download"
        ];

        foreach (string extra in extras)
            DeleteFileCore(extra, log: false);
    }

    private static void ExtractZip(
        string archivePath,
        string dest,
        IProgress<PatchApplyProgress>? progress,
        CancellationToken cancellationToken)
    {
        dest = Path.GetFullPath(dest);
        using var zip = ZipFile.OpenRead(archivePath);
        var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        int total = entries.Count;
        progress?.Report(new PatchApplyProgress("Extracting", 0, total, null));

        int done = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = Path.GetFullPath(Path.Combine(dest, entry.FullName));
            if (!target.StartsWith(dest, StringComparison.OrdinalIgnoreCase))
                continue;

            string? dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            entry.ExtractToFile(target, overwrite: true);
            done++;
            progress?.Report(new PatchApplyProgress("Extracting", done, total, entry.FullName));
        }
    }

    private static void ExtractWithSharpCompress(
        string archivePath,
        string dest,
        IProgress<PatchApplyProgress>? progress,
        CancellationToken cancellationToken)
    {
        dest = Path.GetFullPath(dest);
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
        int total = entries.Count;
        progress?.Report(new PatchApplyProgress("Extracting", 0, total, null));

        var options = new ExtractionOptions
        {
            ExtractFullPath = true,
            Overwrite = true
        };

        int done = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry.WriteToDirectory(dest, options);
            done++;
            progress?.Report(new PatchApplyProgress("Extracting", done, total, entry.Key));
        }
    }

    private static async Task ExtractSevenZipAsync(string archivePath, string dest, CancellationToken cancellationToken)
    {
        string? sevenZip = FindSevenZip();
        if (sevenZip is not null)
        {
            var start = new ProcessStartInfo
            {
                FileName = sevenZip,
                Arguments = $"x -y -o\"{dest}\" -- \"{archivePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start 7-Zip.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode == 0)
                return;

            string error = await process.StandardError.ReadToEndAsync(cancellationToken);
            AppLog.Write($"[Patch] 7-Zip exit {process.ExitCode}: {error}");
        }

        await Task.Run(() =>
        {
            ArchiveFactory.WriteToDirectory(
                archivePath,
                dest,
                new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true
                });
        }, cancellationToken);
    }

    [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool NativeDeleteFile(string lpFileName);

    private static string? FindSevenZip()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
        ];

        foreach (string path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }
}
