using System.Text.Json;
using EZManifest.Models;

namespace EZManifest.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SettingsPath => AppPaths.SettingsJson;

    public async Task<AppSettings> LoadAsync()
    {
        await EnsureSettingsFileExistsAsync();

        string json = await File.ReadAllTextAsync(SettingsPath);
        if (string.IsNullOrWhiteSpace(json))
            return new AppSettings();

        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0)
                return new AppSettings();
            root = root[0];
        }

        return root.Deserialize<AppSettings>() ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        await File.WriteAllTextAsync(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public async Task EnsureSettingsFileExistsAsync()
    {
        if (File.Exists(SettingsPath))
            return;

        await SaveAsync(new AppSettings());
    }

    public async Task<string> GetDownloadRootAsync()
    {
        var settings = await LoadAsync();
        if (string.IsNullOrWhiteSpace(settings.DownloadPath))
            throw new InvalidOperationException("No download location is configured. Choose one on the Settings page and click Apply.");

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.DownloadPath));
    }

    public async Task<int> GetCdnCellIdAsync()
    {
        var settings = await LoadAsync();
        return settings.CdnCellId < 0 ? 0 : settings.CdnCellId;
    }

    public async Task<int> GetMaxConcurrentChunksAsync()
    {
        var settings = await LoadAsync();
        return ClampConcurrentChunks(settings.MaxConcurrentChunks);
    }

    public static int ClampConcurrentChunks(int value)
    {
        if (value < AppSettings.MinConcurrentChunks)
            return AppSettings.DefaultMaxConcurrentChunks;
        return Math.Clamp(value, AppSettings.MinConcurrentChunks, AppSettings.MaxConcurrentChunksLimit);
    }

    public async Task UpdateAsync(Action<AppSettings> mutate)
    {
        var settings = await LoadAsync();
        mutate(settings);
        await SaveAsync(settings);
    }
}
