using System.Text.Json;
using EZManifest.Models;

namespace EZManifest.Services;

public sealed class GameLibraryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string LibraryPath => AppPaths.ItemsJson;

    public async Task<List<GameEntry>> LoadAsync()
    {
        if (!File.Exists(LibraryPath))
        {
            await SaveAsync(new List<GameEntry>());
            return new List<GameEntry>();
        }

        string json = await File.ReadAllTextAsync(LibraryPath);
        var games = string.IsNullOrWhiteSpace(json)
            ? new List<GameEntry>()
            : JsonSerializer.Deserialize<List<GameEntry>>(json) ?? new List<GameEntry>();

        // Older entries accidentally stored the wide logo; prefer vertical cover when present.
        bool changed = false;
        foreach (var game in games)
        {
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

        if (changed)
            await SaveAsync(games);

        return games;
    }

    public Task SaveAsync(IEnumerable<GameEntry> games) =>
        File.WriteAllTextAsync(LibraryPath, JsonSerializer.Serialize(games, JsonOptions));

    public async Task UpsertAsync(GameEntry game)
    {
        var games = await LoadAsync();
        var existing = games.FirstOrDefault(item => item.AppId == game.AppId);
        games.RemoveAll(item => item.AppId == game.AppId);

        if (string.IsNullOrWhiteSpace(game.StartLocation))
            game.StartLocation = existing?.StartLocation ?? string.Empty;
        if (string.IsNullOrWhiteSpace(game.InstallPath))
            game.InstallPath = existing?.InstallPath ?? string.Empty;

        games.Add(game);
        await SaveAsync(games);
    }

    public async Task RemoveAsync(string appId)
    {
        var games = await LoadAsync();
        if (games.RemoveAll(game => game.AppId == appId) > 0)
            await SaveAsync(games);
    }
}
