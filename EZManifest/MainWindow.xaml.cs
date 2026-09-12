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
    private readonly GameLibraryService _gameLibrary;
    private readonly AppUpdateService _updateService;
    private readonly FileExplorerPickerService _filePicker;
    private bool _startupInstallPromptShown;
    private bool _centeredOnStartup;
    private bool _allowClose;
    private bool _closePromptOpen;

    public MainWindow(
        IServiceProvider services,
        AppMessageBoxService messageBoxService,
        AppNotificationService notificationService,
        AppSettingsService settingsService,
        DebugLogService debugLogService,
        WindowProvider windowProvider,
        AppNavigationService navigationService,
        GameLibraryService gameLibrary,
        AppUpdateService updateService,
        FileExplorerPickerService filePicker)
    {
        _services = services;
        _messageBoxService = messageBoxService;
        _notificationService = notificationService;
        _settingsService = settingsService;
        _debugLogService = debugLogService;
        _windowProvider = windowProvider;
        _navigationService = navigationService;
        _gameLibrary = gameLibrary;
        _updateService = updateService;
        _filePicker = filePicker;

        InitializeComponent();

        AppWindow.Closing += OnAppWindowClosing;

        ApplyMinimumWindowSize();

        _windowProvider.SetWindow(this);
        _navigationService.Register(NavigateTo);
        RefreshService.OnListRefreshRequested += HandleRefresh;
        _ = UpdateLibraryCountAsync();
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
                await LoadLibraryFilterSettingAsync();
                await ShowWelcomeGuideIfNeededAsync();
                await PromptForInstallLocationIfNeededAsync();
                await CheckForAppUpdateOnStartupAsync();
                await UpdateLibraryCountAsync();
            };
        }

        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        NavigateTo("Library");
    }

    private async Task ShowWelcomeGuideIfNeededAsync()
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            if (settings.HasSeenWelcomeGuide)
                return;

            await ShowWelcomeGuideAsync();
            await _settingsService.UpdateAsync(s => s.HasSeenWelcomeGuide = true);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Welcome guide failed");
        }
    }

    private async Task ShowWelcomeGuideAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Welcome to EZManifest",
            Content = new TextBlock
            {
                Text =
                    "1. Pick a manifest zip file or folder from the Installs section. These can be obtained from all sorts of websites. EZManifest lets you use your own manifests or DepotBox, which is built in. There will be a pop-up on how to use DepotBox.\n\n" +
                    "2. Once you have chosen your game and installed it, you can start playing. If the game has more sophisticated DRM than Steam DRM, use the Apply game fix section. The first-time pop-up there will guide you.\n\n" +
                    "3. Enjoy!\n\n" +
                    "Settings can be tweaked to your liking from the Settings section in the left bar.",
                TextWrapping = TextWrapping.WrapWholeWords
            },
            PrimaryButtonText = "OK",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
            RequestedTheme = Content is FrameworkElement root ? root.ActualTheme : ElementTheme.Default
        };
        dialog.Resources["ContentDialogMinHeight"] = 0.0;
        dialog.Resources["ContentDialogMinWidth"] = 420.0;
        dialog.Resources["ContentDialogMaxWidth"] = 560.0;

        await dialog.ShowAsync();
    }

    private async Task LoadLibraryFilterSettingAsync()
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            ShowDownloadedOnlyCheckBox.Checked -= ShowDownloadedOnlyCheckBox_Changed;
            ShowDownloadedOnlyCheckBox.Unchecked -= ShowDownloadedOnlyCheckBox_Changed;
            ShowDownloadedOnlyCheckBox.IsChecked = settings.ShowDownloadedOnly;
            ShowDownloadedOnlyCheckBox.Checked += ShowDownloadedOnlyCheckBox_Changed;
            ShowDownloadedOnlyCheckBox.Unchecked += ShowDownloadedOnlyCheckBox_Changed;

            LibrarySortMode sortMode = ParseLibrarySortMode(settings.LibrarySortMode);
            ApplyLibrarySortRadios(sortMode);
            ApplyLibraryViewButtons(settings.UseLibraryListView);
            if (ContentFrame.Content is LibraryPage library)
            {
                library.SetShowDownloadedOnly(settings.ShowDownloadedOnly);
                library.SetSortMode(sortMode);
                library.SetLibraryListView(settings.UseLibraryListView);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Failed to load library filter setting");
        }
    }

    private async void LibraryListViewButton_Click(object sender, RoutedEventArgs e) =>
        await SetLibraryListViewAsync(true);

    private async void LibraryGridViewButton_Click(object sender, RoutedEventArgs e) =>
        await SetLibraryListViewAsync(false);

    private async Task SetLibraryListViewAsync(bool useListView)
    {
        ApplyLibraryViewButtons(useListView);

        try
        {
            await _settingsService.UpdateAsync(settings => settings.UseLibraryListView = useListView);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Failed to save library view setting");
        }

        if (ContentFrame.Content is LibraryPage library)
            library.SetLibraryListView(useListView);
    }

    private void ApplyLibraryViewButtons(bool useListView)
    {
        LibraryListViewButton.IsChecked = useListView;
        LibraryGridViewButton.IsChecked = !useListView;
    }

    private async void ShowDownloadedOnlyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        bool showDownloadedOnly = ShowDownloadedOnlyCheckBox.IsChecked == true;

        try
        {
            await _settingsService.UpdateAsync(settings => settings.ShowDownloadedOnly = showDownloadedOnly);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Failed to save library filter setting");
        }

        if (ContentFrame.Content is LibraryPage library)
            library.SetShowDownloadedOnly(showDownloadedOnly);
    }

    private async void LibrarySortRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag })
            return;

        LibrarySortMode sortMode = ParseLibrarySortMode(tag);
        try
        {
            await _settingsService.UpdateAsync(settings => settings.LibrarySortMode = sortMode.ToString());
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Failed to save library sort setting");
        }

        if (ContentFrame.Content is LibraryPage library)
            library.SetSortMode(sortMode);
    }

    private void ApplyLibrarySortRadios(LibrarySortMode sortMode)
    {
        SortNameAscRadio.Checked -= LibrarySortRadio_Checked;
        SortNameDescRadio.Checked -= LibrarySortRadio_Checked;
        SortSizeDescRadio.Checked -= LibrarySortRadio_Checked;
        SortSizeAscRadio.Checked -= LibrarySortRadio_Checked;
        SortInstalledFirstRadio.Checked -= LibrarySortRadio_Checked;

        SortNameAscRadio.IsChecked = sortMode == LibrarySortMode.NameAsc;
        SortNameDescRadio.IsChecked = sortMode == LibrarySortMode.NameDesc;
        SortSizeDescRadio.IsChecked = sortMode == LibrarySortMode.SizeDesc;
        SortSizeAscRadio.IsChecked = sortMode == LibrarySortMode.SizeAsc;
        SortInstalledFirstRadio.IsChecked = sortMode == LibrarySortMode.InstalledFirst;

        SortNameAscRadio.Checked += LibrarySortRadio_Checked;
        SortNameDescRadio.Checked += LibrarySortRadio_Checked;
        SortSizeDescRadio.Checked += LibrarySortRadio_Checked;
        SortSizeAscRadio.Checked += LibrarySortRadio_Checked;
        SortInstalledFirstRadio.Checked += LibrarySortRadio_Checked;
    }

    private static LibrarySortMode ParseLibrarySortMode(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out LibrarySortMode mode)
            ? mode
            : LibrarySortMode.NameAsc;

    private LibrarySortMode CurrentLibrarySortMode()
    {
        if (SortNameDescRadio.IsChecked == true)
            return LibrarySortMode.NameDesc;
        if (SortSizeDescRadio.IsChecked == true)
            return LibrarySortMode.SizeDesc;
        if (SortSizeAscRadio.IsChecked == true)
            return LibrarySortMode.SizeAsc;
        if (SortInstalledFirstRadio.IsChecked == true)
            return LibrarySortMode.InstalledFirst;
        return LibrarySortMode.NameAsc;
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
            return;

        var downloads = _services.GetRequiredService<DownloadsPage>();
        if (!downloads.HasActiveDownloads)
            return;

        args.Cancel = true;
        if (_closePromptOpen)
            return;

        _closePromptOpen = true;
        try
        {
            int count = downloads.ActiveDownloadCount;
            string body = count == 1
                ? "Closing EZManifest will cancel that install and remove the partial files so you don't end up with a broken game.\n\nExit and cancel the download?"
                : $"Closing EZManifest will cancel those installs and remove the partial files so you don't end up with broken games.\n\nExit and cancel the downloads?";

            var result = await _messageBoxService.ShowAsync(
                "Downloads in progress",
                body,
                "Exit and cancel",
                "Stay");

            if (result != ContentDialogResult.Primary)
                return;

            AppLog.Write($"[Window] Closing with {count} active download(s) — cancelling");
            await downloads.CancelAllDownloadsAndWaitAsync();
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Close-while-downloading prompt failed");
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    private void HandleRefresh()
    {
        DispatcherQueue.TryEnqueue(() => _ = UpdateLibraryCountAsync());
    }

    private async Task UpdateLibraryCountAsync()
    {
        try
        {
            var games = await _gameLibrary.LoadAsync();
            int count = games.Count;
            string text = $"({count} game{(count == 1 ? "" : "s")} in library)";
            LibraryCountTextBlock.Text = text;
            ToolTipService.SetToolTip(LibraryCountTextBlock, text);
        }
        catch
        {
            // Ignore if busy
        }
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

        if (GeForceNowHost.TryGetDefaultInstallPath(out string gfnPath))
        {
            await _settingsService.UpdateAsync(current => current.DownloadPath = gfnPath);
            _notificationService.Show(
                "Install location set",
                $"{gfnPath} (GeForce Now)",
                InfoBarSeverity.Success);
            AppLog.Write($"[Startup] GeForce Now detected (C:\\asgard). Install path set to {gfnPath}");
            return;
        }

        var result = await _messageBoxService.ShowAsync(
            "Set install location",
            "No default install location is configured. Choose a folder where downloaded games will be saved.",
            "Choose folder",
            "Later");

        if (result != ContentDialogResult.Primary)
            return;

        string? folder = await _filePicker.PickFolderAsync(
            "Select install folder",
            KnownExplorerFolders.Desktop);
        if (string.IsNullOrWhiteSpace(folder))
            return;

        await _settingsService.UpdateAsync(settings => settings.DownloadPath = folder);
        _notificationService.Show(
            "Install location set",
            folder,
            InfoBarSeverity.Success);
    }

    private async Task CheckForAppUpdateOnStartupAsync()
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            if (!settings.CheckForUpdatesOnStartup)
                return;

            await _updateService.PromptIfAvailableAsync(silentWhenCurrent: true);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "[Update] Startup check failed");
        }
    }

    public async void CloseForUpdate()
    {
        _allowClose = true;
        try
        {
            var downloads = _services.GetRequiredService<DownloadsPage>();
            if (downloads.HasActiveDownloads)
                await downloads.CancelAllDownloadsAndWaitAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "[Update] Cancel downloads before update failed");
        }

        Close();
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
        if (ContentFrame.Content is PatchPage leavingPatch && tag != "Patch")
            leavingPatch.ReleaseBrowser();
        if (ContentFrame.Content is DownloadsPage leavingDownloads && tag != "Downloads")
            leavingDownloads.ReleaseBrowser();

        Page page = tag switch
        {
            "Library" => _services.GetRequiredService<LibraryPage>(),
            "Downloads" => _services.GetRequiredService<DownloadsPage>(),
            "Patch" => _services.GetRequiredService<PatchPage>(),
            "Debug" => _services.GetRequiredService<DebugConsolePage>(),
            "Settings" => _services.GetRequiredService<SettingsPage>(),
            _ => _services.GetRequiredService<LibraryPage>()
        };

        ContentFrame.Content = page;
        SelectNavigationItem(tag);

        TitleBarSearchPanel.Visibility = page is LibraryPage ? Visibility.Visible : Visibility.Collapsed;
        UpdateTitleBarPassthroughRegion();

        if (page is LibraryPage library)
        {
            library.SetShowDownloadedOnly(ShowDownloadedOnlyCheckBox.IsChecked == true);
            library.SetSortMode(CurrentLibrarySortMode());
            library.SetLibraryListView(LibraryListViewButton.IsChecked == true);
            library.ApplySearchFilter(TitleBarSearchBox.Text);
        }
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

    private void TitleBarSearchPanel_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateTitleBarPassthroughRegion();

    private void UpdateTitleBarPassthroughRegion()
    {
        double scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        TitleBarRightPadding.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);

        var nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);

        if (TitleBarSearchPanel.Visibility != Visibility.Visible ||
            TitleBarSearchPanel.ActualWidth <= 0 ||
            TitleBarSearchPanel.ActualHeight <= 0)
        {
            nonClient.ClearRegionRects(NonClientRegionKind.Passthrough);
            return;
        }

        GeneralTransform transform = TitleBarSearchPanel.TransformToVisual(null);
        Rect bounds = transform.TransformBounds(new Rect(
            0,
            0,
            TitleBarSearchPanel.ActualWidth,
            TitleBarSearchPanel.ActualHeight));

        var rect = new RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale),
            (int)Math.Round(bounds.Height * scale));

        nonClient.SetRegionRects(NonClientRegionKind.Passthrough, new[] { rect });
    }
}
