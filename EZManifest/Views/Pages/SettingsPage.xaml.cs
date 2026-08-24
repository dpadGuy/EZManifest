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
    private readonly WindowProvider _windowProvider;
    private bool _suppressCdnSave;

    public SettingsViewModel ViewModel { get; }

    public SettingsPage(
        SettingsViewModel viewModel,
        AppSettingsService settingsService,
        WindowProvider windowProvider)
    {
        ViewModel = viewModel;
        _settingsService = settingsService;
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

            _suppressCdnSave = true;
            CdnRegionComboBox.ItemsSource = SteamCdnRegions.All;
            CdnRegionComboBox.SelectedItem = SteamCdnRegions.Find(settings.CdnCellId);
            _suppressCdnSave = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading settings: {ex.Message}");
            _suppressCdnSave = false;
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
            await _settingsService.UpdateAsync(settings =>
                settings.DownloadPath = DownloadPathTextBox.Text);
        }
        catch (Exception ex)
        {
            await new ContentDialog
            {
                Title = "Could not save settings",
                Content = $"{ex.Message}\n\nPath: {_settingsService.SettingsPath}",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            }.ShowAsync();
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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save CDN region: {ex.Message}");
        }
    }
}
