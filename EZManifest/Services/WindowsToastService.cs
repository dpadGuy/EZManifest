using System.Runtime.InteropServices;
using EZManifest.Models;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace EZManifest.Services;

public sealed class WindowsToastService
{
    public const string AppUserModelId = "dpadGuy.EZManifest";

    private readonly AppSettingsService _settingsService;
    private bool _registered;

    public WindowsToastService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public void Initialize()
    {
        if (_registered)
            return;

        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Windows notification registration failed");
        }
    }

    public async Task NotifyInstallCompleteAsync(string gameName)
    {
        AppSettings settings;
        try
        {
            settings = await _settingsService.LoadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Could not load notification settings");
            return;
        }

        if (!settings.NotifyOnInstallComplete)
            return;

        string name = string.IsNullOrWhiteSpace(gameName) ? "Game" : gameName.Trim();
        Show($"Install complete for {name}");
    }

    private void Show(string title)
    {
        Initialize();
        if (!_registered)
            return;

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Windows notification failed");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);
}
