using EZManifest.Models;

namespace EZManifest.Services;

public sealed class GameUninstallService
{
    private readonly AppSettingsService _settingsService;
    private readonly GameInstallPathService _installPathService;
    private readonly GameLibraryService _gameLibrary;

    public GameUninstallService(
        AppSettingsService settingsService,
        GameInstallPathService installPathService,
        GameLibraryService gameLibrary)
    {
        _settingsService = settingsService;
        _installPathService = installPathService;
        _gameLibrary = gameLibrary;
    }

    public async Task UninstallAsync(GameEntry game)
    {
        string downloadRoot = Path.GetFullPath(await _settingsService.GetDownloadRootAsync());
        string installDirectory = !string.IsNullOrWhiteSpace(game.InstallPath) && Directory.Exists(game.InstallPath)
            ? Path.GetFullPath(game.InstallPath)
            : Path.GetFullPath(await _installPathService.GetInstallDirectoryAsync(game.Name, game.AppId));

        EnsureSafeInstallDirectory(downloadRoot, installDirectory);

        if (Directory.Exists(installDirectory))
            await Task.Run(() => Directory.Delete(installDirectory, recursive: true));

        await _gameLibrary.RemoveAsync(game.AppId);
    }

    private static void EnsureSafeInstallDirectory(string downloadRoot, string installDirectory)
    {
        string relativePath = Path.GetRelativePath(downloadRoot, installDirectory);
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath == "." ||
            relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("The game install folder is outside the configured download location.");
        }
    }
}
