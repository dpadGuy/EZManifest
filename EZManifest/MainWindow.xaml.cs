using EZManifest.Models;
using EZManifest.Services;
using EZManifest.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EZManifest;

public sealed partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly AppMessageBoxService _messageBoxService;
    private readonly AppNotificationService _notificationService;
    private readonly AppSettingsService _settingsService;
    private readonly DebugLogService _debugLogService;
    private readonly WindowProvider _windowProvider;
    private readonly AppNavigationService _navigationService;
    private bool _startupInstallPromptShown;
    private bool _centeredOnStartup;

    public MainWindow(
        IServiceProvider services,
        AppMessageBoxService messageBoxService,
        AppNotificationService notificationService,
        AppSettingsService settingsService,
        DebugLogService debugLogService,
        WindowProvider windowProvider,
        AppNavigationService navigationService)
    {
        _services = services;
        _messageBoxService = messageBoxService;
        _notificationService = notificationService;
        _settingsService = settingsService;
        _debugLogService = debugLogService;
        _windowProvider = windowProvider;
        _navigationService = navigationService;

        InitializeComponent();

        ApplyMinimumWindowSize();

        _windowProvider.SetWindow(this);
        _navigationService.Register(NavigateTo);
        AppLog.Write("EZManifest started.");

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ConfigureCaptionButtonColors();
        ApplyWindowIcon();
        CenterOnScreen();

        Activated += (_, _) =>
        {
            // Re-center once size is known after first show.
            if (_centeredOnStartup)
                return;
            _centeredOnStartup = true;
            CenterOnScreen();
        };

        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) => ConfigureCaptionButtonColors();
            root.Loaded += async (_, _) =>
            {
                if (root.XamlRoot is not null)
                    _messageBoxService.SetXamlRoot(root.XamlRoot);

                _notificationService.Initialize(AppInfoBar, DispatcherQueue);
                ConfigureCaptionButtonColors();
                UpdateTitleBarPassthroughRegion();
                await PromptForInstallLocationIfNeededAsync();
            };
        }

        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        NavigateTo("Library");
    }

    private void ApplyMinimumWindowSize()
    {
        // Keep the shell usable (nav + at least one library card row).
        const int minWidth = 960;
        const int minHeight = 640;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = minWidth;
            presenter.PreferredMinimumHeight = minHeight;
        }

        SizeInt32 size = AppWindow.Size;
        int width = Math.Max(size.Width, minWidth);
        int height = Math.Max(size.Height, minHeight);
        if (width != size.Width || height != size.Height)
            AppWindow.Resize(new SizeInt32(width, height));
    }

    private async Task PromptForInstallLocationIfNeededAsync()
    {
        if (_startupInstallPromptShown)
            return;

        _startupInstallPromptShown = true;

        AppSettings settings;
        try
        {
            settings = await _settingsService.LoadAsync();
        }
        catch
        {
            settings = new AppSettings();
        }

        if (!string.IsNullOrWhiteSpace(settings.DownloadPath))
            return;

        var result = await _messageBoxService.ShowAsync(
            "Set install location",
            "No default install location is configured. Choose a folder where downloaded games will be saved.",
            "Choose folder",
            "Later");

        if (result != ContentDialogResult.Primary)
            return;

        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, _windowProvider.GetWindowHandle());
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return;

        await _settingsService.UpdateAsync(settings => settings.DownloadPath = folder.Path);
        _notificationService.Show(
            "Install location set",
            folder.Path,
            InfoBarSeverity.Success);
    }

    private void ApplyWindowIcon()
    {
        // ApplicationIcon only embeds the .exe icon (Explorer). WinUI still needs
        // AppWindow.SetIcon for the live window / taskbar icon.
        string iconPath = Path.Combine(AppPaths.ExeDirectory, "Assets", "EZManifestLogo.ico");
        if (!File.Exists(iconPath))
            iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "EZManifestLogo.ico");
        if (!File.Exists(iconPath))
            return;

        AppWindow.SetIcon(iconPath);
        AppWindow.SetTitleBarIcon(iconPath);
        AppWindow.SetTaskbarIcon(iconPath);
    }

    private void CenterOnScreen()
    {
        DisplayArea? displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (displayArea is null)
            return;

        RectInt32 workArea = displayArea.WorkArea;
        SizeInt32 size = AppWindow.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return;

        AppWindow.Move(new PointInt32(
            workArea.X + (workArea.Width - size.Width) / 2,
            workArea.Y + (workArea.Height - size.Height) / 2));
    }

    public void ConfigureCaptionButtonColors()
    {
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        bool isDark = Content is FrameworkElement root
            ? root.ActualTheme == ElementTheme.Dark
            : Application.Current.RequestedTheme == ApplicationTheme.Dark;

        if (isDark)
        {
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 160, 160, 160);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedForegroundColor = Colors.White;
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(24, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 255, 255, 255);
        }
        else
        {
            titleBar.ButtonForegroundColor = Colors.Black;
            titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 96, 96, 96);
            titleBar.ButtonHoverForegroundColor = Colors.Black;
            titleBar.ButtonPressedForegroundColor = Colors.Black;
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(24, 0, 0, 0);
            titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 0, 0, 0);
        }
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            NavigateTo(tag);
    }

    private void NavigateTo(string tag)
    {
        Page page = tag switch
        {
            "Library" => _services.GetRequiredService<LibraryPage>(),
            "Downloads" => _services.GetRequiredService<DownloadsPage>(),
            "Debug" => _services.GetRequiredService<DebugConsolePage>(),
            "Settings" => _services.GetRequiredService<SettingsPage>(),
            _ => _services.GetRequiredService<LibraryPage>()
        };

        ContentFrame.Content = page;
        SelectNavigationItem(tag);

        TitleBarSearchBox.Visibility = page is LibraryPage ? Visibility.Visible : Visibility.Collapsed;
        UpdateTitleBarPassthroughRegion();

        if (page is LibraryPage library)
            library.ApplySearchFilter(TitleBarSearchBox.Text);
    }

    private void SelectNavigationItem(string tag)
    {
        foreach (object item in RootNavigation.MenuItems)
        {
            if (item is NavigationViewItem navItem &&
                navItem.Tag is string itemTag &&
                string.Equals(itemTag, tag, StringComparison.OrdinalIgnoreCase))
            {
                if (!ReferenceEquals(RootNavigation.SelectedItem, navItem))
                    RootNavigation.SelectedItem = navItem;
                return;
            }
        }
    }

    private void TitleBarSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        if (ContentFrame.Content is LibraryPage library)
            library.ApplySearchFilter(sender.Text);
    }

    private void AppTitleBar_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateTitleBarPassthroughRegion();

    private void TitleBarSearchBox_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateTitleBarPassthroughRegion();

    private void UpdateTitleBarPassthroughRegion()
    {
        double scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        TitleBarRightPadding.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);

        var nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);

        if (TitleBarSearchBox.Visibility != Visibility.Visible ||
            TitleBarSearchBox.ActualWidth <= 0 ||
            TitleBarSearchBox.ActualHeight <= 0)
        {
            nonClient.ClearRegionRects(NonClientRegionKind.Passthrough);
            return;
        }

        GeneralTransform transform = TitleBarSearchBox.TransformToVisual(null);
        Rect bounds = transform.TransformBounds(new Rect(
            0,
            0,
            TitleBarSearchBox.ActualWidth,
            TitleBarSearchBox.ActualHeight));

        var rect = new RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale),
            (int)Math.Round(bounds.Height * scale));

        nonClient.SetRegionRects(NonClientRegionKind.Passthrough, new[] { rect });
    }
}
