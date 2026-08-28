using System.IO.Compression;
using System.Net.Http;

namespace EZManifest.Services;

public sealed class GoldbergPatchService
{
    public const string SteamApiDll = "steam_api.dll";
    public const string SteamApi64Dll = "steam_api64.dll";

    private const string ArtifactUrl =
        "https://gitlab.com/Mr_Goldberg/goldberg_emulator/-/jobs/4247811310/artifacts/download";

    private readonly SemaphoreSlim _ensureGate = new(1, 1);

    public string GoldbergDirectory => Path.Combine(AppPaths.ExeDirectory, "Goldberg");

    public async Task EnsureGoldbergAsync(CancellationToken cancellationToken = default)
    {
        await _ensureGate.WaitAsync(cancellationToken);
        try
        {
            if (TryFindGoldbergDll(GoldbergDirectory, SteamApiDll) is not null ||
                TryFindGoldbergDll(GoldbergDirectory, SteamApi64Dll) is not null)
            {
                return;
            }

            Directory.CreateDirectory(GoldbergDirectory);

            string zipPath = Path.Combine(Path.GetTempPath(), $"goldberg_{Guid.NewGuid():N}.zip");
            try
            {
                using var downloadClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd("EZManifest");

                using (var response = await downloadClient.GetAsync(
                           ArtifactUrl,
                           HttpCompletionOption.ResponseHeadersRead,
                           cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    await using var fs = File.Create(zipPath);
                    await response.Content.CopyToAsync(fs, cancellationToken);
                }

                // Fresh extract into Goldberg\
                foreach (string entry in Directory.EnumerateFileSystemEntries(GoldbergDirectory))
                {
                    if (Directory.Exists(entry))
                        Directory.Delete(entry, recursive: true);
                    else
                        File.Delete(entry);
                }

                ZipFile.ExtractToDirectory(zipPath, GoldbergDirectory);
            }
            finally
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
            }

            if (TryFindGoldbergDll(GoldbergDirectory, SteamApiDll) is null &&
                TryFindGoldbergDll(GoldbergDirectory, SteamApi64Dll) is null)
            {
                throw new InvalidDataException(
                    "Goldberg was downloaded, but steam_api.dll / steam_api64.dll were not found in the archive.");
            }
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    public IReadOnlyList<string> FindSteamApiFolders(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            return Array.Empty<string>();

        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.EnumerateFiles(gameRoot, SteamApiDll, SearchOption.AllDirectories))
            folders.Add(Path.GetDirectoryName(file)!);

        foreach (string file in Directory.EnumerateFiles(gameRoot, SteamApi64Dll, SearchOption.AllDirectories))
            folders.Add(Path.GetDirectoryName(file)!);

        return folders.ToList();
    }

    public async Task PatchFoldersAsync(
        IEnumerable<string> targetFolders,
        string appId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appId))
            throw new ArgumentException("A Steam AppID is required to write steam_appid.txt.", nameof(appId));

        await EnsureGoldbergAsync(cancellationToken);

        string? sourceApi = TryFindGoldbergDll(GoldbergDirectory, SteamApiDll);
        string? sourceApi64 = TryFindGoldbergDll(GoldbergDirectory, SteamApi64Dll);

        if (sourceApi is null && sourceApi64 is null)
            throw new InvalidOperationException("Goldberg DLLs are missing.");

        string appIdText = appId.Trim();

        foreach (string folder in targetFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder))
                continue;

            if (sourceApi is not null)
                ReplaceWithBackup(Path.Combine(folder, SteamApiDll), sourceApi);

            if (sourceApi64 is not null)
                ReplaceWithBackup(Path.Combine(folder, SteamApi64Dll), sourceApi64);

            // Beside the patched steam_api DLLs (Goldberg also checks steam_settings\).
            await File.WriteAllTextAsync(
                Path.Combine(folder, "steam_appid.txt"),
                appIdText,
                cancellationToken);

            string settingsDir = Path.Combine(folder, "steam_settings");
            Directory.CreateDirectory(settingsDir);
            await File.WriteAllTextAsync(
                Path.Combine(settingsDir, "steam_appid.txt"),
                appIdText,
                cancellationToken);
        }
    }

    private static string? TryFindGoldbergDll(string goldbergRoot, string fileName)
    {
        if (!Directory.Exists(goldbergRoot))
            return null;

        // Prefer a root-level file, then first match under the tree.
        string rootCandidate = Path.Combine(goldbergRoot, fileName);
        if (File.Exists(rootCandidate))
            return rootCandidate;

        return Directory
            .EnumerateFiles(goldbergRoot, fileName, SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .FirstOrDefault();
    }

    private static void ReplaceWithBackup(string targetDllPath, string sourceDllPath)
    {
        if (!File.Exists(targetDllPath))
            return;

        string backupPath = targetDllPath + ".bak";
        if (File.Exists(backupPath))
            File.Delete(backupPath);

        File.Move(targetDllPath, backupPath);
        File.Copy(sourceDllPath, targetDllPath, overwrite: true);
    }
}
