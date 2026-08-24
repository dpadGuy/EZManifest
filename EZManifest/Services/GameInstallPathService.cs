namespace EZManifest.Services;

public sealed class GameInstallPathService
{
    private readonly AppSettingsService _settingsService;

    public GameInstallPathService(AppSettingsService settingsService) =>
        _settingsService = settingsService;

    public async Task<string> GetInstallDirectoryAsync(string gameTitle, string appId)
    {
        string downloadRoot = await _settingsService.GetDownloadRootAsync();
        string safeTitle = SanitizeDirectoryName(gameTitle);
        return Path.Combine(downloadRoot, $"{safeTitle} - {appId}");
    }

    private static string SanitizeDirectoryName(string value)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        string sanitized = new(value
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());

        sanitized = sanitized.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "Steam Game" : sanitized;
    }
}
