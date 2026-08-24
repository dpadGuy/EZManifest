using System.Net.Http;
using EZManifest.Services;
using EZManifest.ViewModels;
using EZManifest.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace EZManifest;

public partial class App : Application
{
    private Window? _window;
    private readonly IServiceProvider _services;

    public App()
    {
        InitializeComponent();
        _services = ConfigureServices();
    }

    public static new App Current => (App)Application.Current;

    public IServiceProvider Services => _services;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = _services.GetRequiredService<MainWindow>();
        _window.Activate();
    }

    public void SetTheme(ElementTheme theme)
    {
        if (_window?.Content is FrameworkElement root)
            root.RequestedTheme = theme;
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<HttpClient>();
        services.AddSingleton<WindowProvider>();
        services.AddSingleton<AppNotificationService>();
        services.AddSingleton<AppMessageBoxService>();
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<GameLibraryService>();
        services.AddSingleton<LuaManifestParser>();
        services.AddSingleton<GoldbergPatchService>();
        services.AddSingleton<ManifestArchiveService>();
        services.AddSingleton<SteamMetadataService>();
        services.AddSingleton<SteamDepotMetadataService>();
        services.AddSingleton<GameInstallPathService>();
        services.AddSingleton<GameUninstallService>();

        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<LibraryPage>();
        services.AddSingleton<DownloadsPage>();
        services.AddSingleton<SettingsPage>();

        return services.BuildServiceProvider();
    }
}
