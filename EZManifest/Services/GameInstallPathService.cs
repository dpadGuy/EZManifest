using Microsoft.UI.Xaml.Controls;

namespace EZManifest.Services;

public sealed class GameInstallPathService
{
    private readonly AppSettingsService _settingsService;
    private readonly AppMessageBoxService _messageBoxService;
    private readonly FileExplorerPickerService _filePicker;

    public GameInstallPathService(
        AppSettingsService settingsService,
        AppMessageBoxService messageBoxService,
        FileExplorerPickerService filePicker)
    {
        _settingsService = settingsService;
        _messageBoxService = messageBoxService;
        _filePicker = filePicker;
    }

    public async Task<string> GetInstallDirectoryAsync(
        string gameTitle,
        string appId,
        bool promptIfMissing = true)
    {
        string? downloadRoot = promptIfMissing
            ? await TryEnsureDownloadRootAsync()
            : await TryGetConfiguredRootAsync();
        if (string.IsNullOrWhiteSpace(downloadRoot))
            throw new InvalidOperationException(
                "No download location is configured. Choose one on the Settings page and click Apply.");

        string safeTitle = SanitizeDirectoryName(gameTitle);
        return Path.Combine(downloadRoot, $"{safeTitle} - {appId}");
    }

    public async Task<string?> TryEnsureDownloadRootAsync()
    {
        string? configured = await TryGetConfiguredRootAsync();
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        if (GeForceNowHost.TryGetDefaultInstallPath(out string gfnPath))
        {
            await _settingsService.UpdateAsync(settings => settings.DownloadPath = gfnPath);
            AppLog.Write($"[InstallPath] GeForce Now detected. Install path set to {gfnPath}");
            return Path.GetFullPath(gfnPath);
        }

        var result = await _messageBoxService.ShowAsync(
            "Set install location",
            "No install folder is configured, or the previous folder no longer exists. Choose where downloaded games will be saved.",
            "Choose folder",
            "Cancel");
        if (result != ContentDialogResult.Primary)
            return null;

        string? folder = await _filePicker.PickFolderAsync(
            "Select install folder",
            KnownExplorerFolders.Desktop);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return null;

        string full = Path.GetFullPath(folder);
        await _settingsService.UpdateAsync(settings => settings.DownloadPath = full);
        AppLog.Write($"[InstallPath] Install path set to {full}");
        return full;
    }

    private async Task<string?> TryGetConfiguredRootAsync()
    {
        var settings = await _settingsService.LoadAsync();
        string configured = settings.DownloadPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        try
        {
            string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
            return Directory.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeDirectoryName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Steam Game";

        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        string sanitized = new(value
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());

        sanitized = sanitized.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "Steam Game" : sanitized;
    }
}
