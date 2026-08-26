using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZManifest.Services;
using Microsoft.UI.Xaml;

namespace EZManifest.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsService _settingsService;

    [ObservableProperty]
    private string _appVersion = string.Empty;

    [ObservableProperty]
    private ElementTheme _currentTheme = ElementTheme.Default;

    public SettingsViewModel(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
        AppVersion = GetAssemblyVersion();
        CurrentTheme = Application.Current.RequestedTheme == ApplicationTheme.Dark
            ? ElementTheme.Dark
            : ElementTheme.Light;
        _ = SyncThemeFromSettingsAsync();
    }

    private static string GetAssemblyVersion() =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty;

    private async Task SyncThemeFromSettingsAsync()
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            ElementTheme theme = ParseTheme(settings.Theme);
            if (theme is ElementTheme.Light or ElementTheme.Dark)
                CurrentTheme = theme;
        }
        catch
        {
            // Keep constructor fallback theme.
        }
    }

    [RelayCommand]
    private async Task ChangeTheme(string parameter)
    {
        ElementTheme theme = parameter switch
        {
            "theme_light" => ElementTheme.Light,
            "theme_dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        if (theme == ElementTheme.Default || CurrentTheme == theme)
            return;

        if (Application.Current is App app)
            app.SetTheme(theme);

        CurrentTheme = theme;

        try
        {
            await _settingsService.UpdateAsync(settings => settings.Theme = ToStorageValue(theme));
            AppLog.Write($"[Settings] Theme saved: {theme}");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Failed to save theme");
        }
    }

    public static ElementTheme ParseTheme(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

    public static string ToStorageValue(ElementTheme theme) => theme switch
    {
        ElementTheme.Light => "Light",
        ElementTheme.Dark => "Dark",
        _ => string.Empty
    };
}
