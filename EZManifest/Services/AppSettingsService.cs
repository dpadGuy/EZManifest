using System.Text.Json;
using EZManifest.Models;

namespace EZManifest.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SettingsPath => AppPaths.SettingsJson;

    public event Action? ManifestSourceSettingsChanged;

    public void NotifyManifestSourceChanged() => ManifestSourceSettingsChanged?.Invoke();

    public async Task<AppSettings> LoadAsync()
    {
        await EnsureSettingsFileExistsAsync();

        string json = await File.ReadAllTextAsync(SettingsPath);
        if (string.IsNullOrWhiteSpace(json))
            return CreateDefaultSettings();

        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0)
                return CreateDefaultSettings();
            root = root[0];
        }

        var settings = root.Deserialize<AppSettings>() ?? CreateDefaultSettings();
        await PersistClampedChunksIfNeededAsync(settings);
        return settings;
    }

    public static int GetDefaultConcurrentChunks() =>
        GeForceNowHost.IsDetected
            ? AppSettings.GeForceNowDefaultMaxConcurrentChunks
            : AppSettings.DefaultMaxConcurrentChunks;

    private static AppSettings CreateDefaultSettings() =>
        new() { MaxConcurrentChunks = GetDefaultConcurrentChunks() };

    private async Task PersistClampedChunksIfNeededAsync(AppSettings settings)
    {
        int clamped = ClampConcurrentChunks(settings.MaxConcurrentChunks);
        if (settings.MaxConcurrentChunks == clamped)
            return;

        settings.MaxConcurrentChunks = clamped;
        await SaveAsync(settings);
        AppLog.Write($"[Settings] Concurrent chunks set to {clamped} (limit {AppSettings.MaxConcurrentChunksLimit})");
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

        await SaveAsync(CreateDefaultSettings());
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
            return GetDefaultConcurrentChunks();
        return Math.Clamp(value, AppSettings.MinConcurrentChunks, AppSettings.MaxConcurrentChunksLimit);
    }

    public const string DefaultManifestSourceUrl = "https://depotbox.org/";

    public static bool TryNormalizeHttpUrl(string? text, out string url)
    {
        url = string.Empty;
        string value = text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!value.Contains("://", StringComparison.Ordinal))
            value = "https://" + value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        url = uri.ToString();
        return true;
    }

    public static bool UsesPreferredManifestSource(AppSettings settings) =>
        settings.UsePreferredManifestSource &&
        TryNormalizeHttpUrl(settings.PreferredManifestSourceUrl, out _);

    public async Task<(bool Preferred, string Url)> GetManifestSourceAsync()
    {
        var settings = await LoadAsync();
        if (UsesPreferredManifestSource(settings) &&
            TryNormalizeHttpUrl(settings.PreferredManifestSourceUrl, out string url))
        {
            return (true, url);
        }

        return (false, DefaultManifestSourceUrl);
    }

    public async Task UpdateAsync(Action<AppSettings> mutate)
    {
        var settings = await LoadAsync();
        mutate(settings);
        await SaveAsync(settings);
    }
}
