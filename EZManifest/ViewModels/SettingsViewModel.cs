using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;

namespace EZManifest.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private string _appVersion = string.Empty;

    [ObservableProperty]
    private ElementTheme _currentTheme = ElementTheme.Default;

    public SettingsViewModel()
    {
        AppVersion = GetAssemblyVersion();
        CurrentTheme = Application.Current.RequestedTheme == ApplicationTheme.Dark
            ? ElementTheme.Dark
            : ElementTheme.Light;
    }

    private static string GetAssemblyVersion() =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty;

    [RelayCommand]
    private void ChangeTheme(string parameter)
    {
        ElementTheme theme = parameter switch
        {
            "theme_light" => ElementTheme.Light,
            "theme_dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        if (CurrentTheme == theme)
            return;

        if (Application.Current is App app)
            app.SetTheme(theme);

        CurrentTheme = theme;
    }
}
