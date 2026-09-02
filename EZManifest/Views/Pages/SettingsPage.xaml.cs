using EZManifest.Models;
using EZManifest.Services;
using EZManifest.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EZManifest.Views.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly AppSettingsService _settingsService;
    private readonly AppMessageBoxService _messageBoxService;
    private readonly AppUpdateService _updateService;
    private readonly WindowProvider _windowProvider;
    private bool _suppressCdnSave;
    private bool _suppressNotifySave = true;
    private bool _suppressUpdateSave = true;

    public SettingsViewModel ViewModel { get; }

    public SettingsPage(
        SettingsViewModel viewModel,
        AppSettingsService settingsService,
        AppMessageBoxService messageBoxService,
        AppUpdateService updateService,
        WindowProvider windowProvider)
    {
        ViewModel = viewModel;
        _settingsService = settingsService;
        _messageBoxService = messageBoxService;
        _updateService = updateService;
        _windowProvider = windowProvider;
        InitializeComponent();
        _ = LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            if (!string.IsNullOrWhiteSpace(settings.DownloadPath))
                DownloadPathTextBox.Text = settings.DownloadPath;

            MaxChunksTextBox.Text = AppSettingsService.ClampConcurrentChunks(settings.MaxConcurrentChunks).ToString();

            _suppressCdnSave = true;
            CdnRegionComboBox.ItemsSource = SteamCdnRegions.All;
            CdnRegionComboBox.SelectedItem = SteamCdnRegions.Find(settings.CdnCellId);
            _suppressCdnSave = false;

            NotifyOnInstallToggle.IsOn = settings.NotifyOnInstallComplete;
            CheckForUpdatesToggle.IsOn = settings.CheckForUpdatesOnStartup;
            _suppressNotifySave = false;
            _suppressUpdateSave = false;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Error loading settings");
            _suppressCdnSave = false;
            _suppressNotifySave = false;
            _suppressUpdateSave = false;
        }
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, _windowProvider.GetWindowHandle());
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            DownloadPathTextBox.Text = folder.Path;
    }

    private async void ApplyPathButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = DownloadPathTextBox.Text?.Trim() ?? string.Empty;
            await _settingsService.UpdateAsync(settings =>
                settings.DownloadPath = path);
            DownloadPathTextBox.Text = path;
            AppLog.Write($"[Settings] Download path saved: {path}");

            await _messageBoxService.ShowAsync(
                "Path applied",
                string.IsNullOrWhiteSpace(path)
                    ? "Download path cleared. New downloads will use the default location."
                    : $"Download path set to:\n{path}\n\nNew downloads will use this folder.");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Failed to save download path");
            await _messageBoxService.ShowAsync(
                "Could not save settings",
                $"{ex.Message}\n\nPath: {_settingsService.SettingsPath}");
        }
    }

    private async void ApplyChunksButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MaxChunksTextBox.Text?.Trim(), out int requested) ||
            requested < AppSettings.MinConcurrentChunks ||
            requested > AppSettings.MaxConcurrentChunksLimit)
        {
            await _messageBoxService.ShowAsync(
                "Invalid value",
                $"Enter a whole number between {AppSettings.MinConcurrentChunks} and {AppSettings.MaxConcurrentChunksLimit}.");
            return;
        }

        await SaveConcurrentChunksAsync(requested);
    }

    private async void RestoreChunksDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveConcurrentChunksAsync(AppSettings.DefaultMaxConcurrentChunks);
    }

    private async Task SaveConcurrentChunksAsync(int requested)
    {
        int clamped = AppSettingsService.ClampConcurrentChunks(requested);

        try
        {
            await _settingsService.UpdateAsync(settings => settings.MaxConcurrentChunks = clamped);
            MaxChunksTextBox.Text = clamped.ToString();
            AppLog.Write($"[Settings] Max concurrent chunks set to {clamped}");

            string message = clamped == AppSettings.DefaultMaxConcurrentChunks
                ? $"Download concurrency restored to the default of {clamped} chunk(s).\n\nNew downloads will use this value. A download already in progress keeps its previous setting."
                : $"Download concurrency is now {clamped} chunk(s).\n\nNew downloads will use this value. A download already in progress keeps its previous setting.";

            await _messageBoxService.ShowAsync("Setting applied", message);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Failed to save concurrent chunks");
            await _messageBoxService.ShowAsync(
                "Could not save settings",
                $"{ex.Message}\n\nPath: {_settingsService.SettingsPath}");
        }
    }

    private async void CdnRegionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCdnSave)
            return;

        if (CdnRegionComboBox.SelectedItem is not SteamCdnRegion region)
            return;

        try
        {
            await _settingsService.UpdateAsync(settings => settings.CdnCellId = region.CellId);
            AppLog.Write($"[Settings] CDN region saved: {region.DisplayName} cellId={region.CellId}");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Failed to save CDN region");
        }
    }

    private async void NotifyOnInstallToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressNotifySave)
            return;

        bool enabled = NotifyOnInstallToggle.IsOn;
        try
        {
            await _settingsService.UpdateAsync(settings => settings.NotifyOnInstallComplete = enabled);
            AppLog.Write($"[Settings] Install notifications {(enabled ? "enabled" : "disabled")}");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Failed to save notification setting");
        }
    }

    private async void CheckForUpdatesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressUpdateSave)
            return;

        bool enabled = CheckForUpdatesToggle.IsOn;
        try
        {
            await _settingsService.UpdateAsync(settings => settings.CheckForUpdatesOnStartup = enabled);
            AppLog.Write($"[Settings] Startup update check {(enabled ? "enabled" : "disabled")}");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Failed to save update setting");
        }
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        await _updateService.PromptIfAvailableAsync(silentWhenCurrent: false);
    }
}
