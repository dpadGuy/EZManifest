using System.Text.Json;
using EZManifest.Models;

namespace EZManifest.Services;

public sealed class GameLibraryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string LibraryPath => AppPaths.ItemsJson;

    public async Task<List<GameEntry>> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var games = await ReadUnlockedAsync();
            if (MigrateInPlace(games))
                await WriteUnlockedAsync(games);
            return games;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<GameEntry> games)
    {
        await _gate.WaitAsync();
        try
        {
            await WriteUnlockedAsync(games);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(GameEntry game)
    {
        await _gate.WaitAsync();
        try
        {
            var games = await ReadUnlockedAsync();
            var existing = games.FirstOrDefault(item => item.AppId == game.AppId);
            games.RemoveAll(item => item.AppId == game.AppId);

            if (string.IsNullOrWhiteSpace(game.StartLocation))
                game.StartLocation = existing?.StartLocation ?? string.Empty;
            if (string.IsNullOrWhiteSpace(game.LaunchOptions))
                game.LaunchOptions = existing?.LaunchOptions ?? string.Empty;
            if (string.IsNullOrWhiteSpace(game.InstallPath))
                game.InstallPath = existing?.InstallPath ?? string.Empty;
            if (game.InstallSizeBytes is not > 0)
                game.InstallSizeBytes = existing?.InstallSizeBytes;
            if (string.IsNullOrWhiteSpace(game.AboutTheGame))
                game.AboutTheGame = existing?.AboutTheGame ?? string.Empty;

            games.Add(game);
            await WriteUnlockedAsync(games);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string appId)
    {
        await _gate.WaitAsync();
        try
        {
            var games = await ReadUnlockedAsync();
            if (games.RemoveAll(game => game.AppId == appId) > 0)
                await WriteUnlockedAsync(games);
        }
        finally
        {
            _gate.Release();
        }

        ManifestArchiveService.DeleteExtractionDirectories(appId);
    }

    private async Task<List<GameEntry>> ReadUnlockedAsync()
    {
        if (!File.Exists(LibraryPath))
        {
            await WriteUnlockedAsync(new List<GameEntry>());
            return new List<GameEntry>();
        }

        string json = await File.ReadAllTextAsync(LibraryPath);
        return string.IsNullOrWhiteSpace(json)
            ? new List<GameEntry>()
            : JsonSerializer.Deserialize<List<GameEntry>>(json) ?? new List<GameEntry>();
    }

    private async Task WriteUnlockedAsync(IEnumerable<GameEntry> games)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        string json = JsonSerializer.Serialize(games, JsonOptions);
        string tempPath = LibraryPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, LibraryPath, overwrite: true);
    }

    private static bool MigrateInPlace(List<GameEntry> games)
    {
        bool changed = false;
        foreach (var game in games)
        {
            string relocatedImage = AppPaths.RelocateStoredPath(game.Image);
            if (!string.Equals(relocatedImage, game.Image, StringComparison.OrdinalIgnoreCase))
            {
                game.Image = relocatedImage;
                changed = true;
            }

            bool hasInstallContent = !string.IsNullOrWhiteSpace(game.InstallPath)
                && Directory.Exists(game.InstallPath)
                && Directory.EnumerateFileSystemEntries(game.InstallPath).Any();

            if (!game.IsInstalled && hasInstallContent)
            {
                game.IsInstalled = true;
                changed = true;
            }
            else if (game.IsInstalled && !hasInstallContent)
            {
                game.IsInstalled = false;
                game.InstallPath = string.Empty;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(game.Image))
                continue;

            string fileName = Path.GetFileName(game.Image);
            if (!fileName.Equals("GameLogo.png", StringComparison.OrdinalIgnoreCase))
                continue;

            string coverPath = Path.Combine(Path.GetDirectoryName(game.Image) ?? string.Empty, "VerticalCoverArt.jpg");
            if (!File.Exists(coverPath))
                continue;

            game.Image = coverPath;
            changed = true;
        }

        return changed;
    }
}
