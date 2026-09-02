using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using EZManifest.Models;
using EZManifest.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.System;
using SteamVideoHttpClient = Windows.Web.Http.HttpClient;

namespace EZManifest.Views.Pages;

public sealed partial class LibraryPage : Page, INotifyPropertyChanged
{
    public static readonly DependencyProperty CoverArtHeightProperty =
        DependencyProperty.Register(
            nameof(CoverArtHeight),
            typeof(double),
            typeof(LibraryPage),
            new PropertyMetadata(270.0));

    private readonly GameLibraryService _gameLibrary;
    private readonly GameInstallSizeService _installSizeService;
    private readonly GameUninstallService _uninstallService;
    private readonly AppMessageBoxService _messageBoxService;
    private readonly GameInstallPathService _installPathService;
    private readonly PostDownloadService _postDownloadService;
    private readonly ShortcutService _shortcutService;
    private readonly CoverArtCache _coverArtCache;
    private readonly SteamMetadataService _steamMetadata;
    private readonly AppNotificationService _notifications;
    private readonly AppNavigationService _navigation;
    private readonly IServiceProvider _services;
    private readonly HashSet<string> _selectedAppIds = new(StringComparer.OrdinalIgnoreCase);

    private bool _hasLoaded;
    private bool _refreshPending;
    private int _loadVersion;
    private Task? _loadTask;
    private string _searchQuery = string.Empty;
    private bool _showDownloadedOnly;
    private bool _useListView = true;
    private bool _suppressListSelectionChanged;
    private int _selectionAnchorIndex = -1;
    private GameEntry? _selectedGame;
    private int _heroLoadVersion;
    private int _mediaIndex;
    private int _mediaPageStart;
    private GameMediaItem? _theaterItem;
    private int _theaterSourceIndex;
    private int _theaterPlayVersion;
    private bool _theaterMediaFailedHooked;
    private static readonly Lazy<SteamVideoHttpClient> SteamVideoHttp = new(CreateSteamVideoHttp);
    private readonly Dictionary<string, DateTime> _recentLaunches = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _launchingExes = new(StringComparer.OrdinalIgnoreCase);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _runningTimer;
    private DownloadsPage? _downloadsPage;
    private bool _downloadsHooked;

    private static readonly GameEntry EmptySelection = new();

    public ObservableCollection<GameEntry> AppsList { get; } = new();
    public ObservableCollection<GameEntry> FilteredApps { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;

    public GameEntry SelectedGame => _selectedGame ?? EmptySelection;
    public string SelectedGameStatus =>
        _selectedGame is null
            ? string.Empty
            : _selectedGame.IsInstalling
                ? "Installing"
                : _selectedGame.IsInstalled
                    ? "Installed"
                    : "Not installed";
    public Visibility HasSelectedGameVisibility =>
        _selectedGame is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoSelectedGameVisibility =>
        _selectedGame is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>2:3 cover height from column width — updated on window resize only.</summary>
    public double CoverArtHeight
    {
        get => (double)GetValue(CoverArtHeightProperty);
        set => SetValue(CoverArtHeightProperty, value);
    }

    public LibraryPage(
        GameLibraryService gameLibrary,
        GameInstallSizeService installSizeService,
        GameUninstallService uninstallService,
        AppMessageBoxService messageBoxService,
        GameInstallPathService installPathService,
        PostDownloadService postDownloadService,
        ShortcutService shortcutService,
        CoverArtCache coverArtCache,
        SteamMetadataService steamMetadata,
        AppNotificationService notifications,
        AppNavigationService navigation,
        IServiceProvider services)
    {
        _gameLibrary = gameLibrary;
        _installSizeService = installSizeService;
        _uninstallService = uninstallService;
        _messageBoxService = messageBoxService;
        _installPathService = installPathService;
        _postDownloadService = postDownloadService;
        _shortcutService = shortcutService;
        _coverArtCache = coverArtCache;
        _steamMetadata = steamMetadata;
        _notifications = notifications;
        _navigation = navigation;
        _services = services;

        InitializeComponent();
        Loaded += LibraryPage_Loaded;
        RefreshService.OnListRefreshRequested += HandleRefresh;
    }

    private void LibraryPage_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateCoverArtHeight(LibraryScroll.ViewportWidth > 0 ? LibraryScroll.ViewportWidth : LibraryScroll.ActualWidth);

        EnsureDownloadsHook();
        SyncInstallingFlags();

        // Avoid the double hitch: only load on first show, or when a refresh was requested
        // while we were on another page.
        if (!_hasLoaded || _refreshPending)
            _ = EnsureLoadedAsync(force: _refreshPending);

        EnsureRunningWatcher();
        RestoreVisibleMedia();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        CloseTheaterMode();
        // Keep RefreshService subscription: this page is a singleton and must
        // still refresh while another nav page is visible.
    }

    /// <summary>Attach a cached downscaled cover while the card is realized.</summary>
    private void GamesGrid_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (FindCoverImage(args.Element) is not Image cover)
            return;

        if (args.Element is FrameworkElement { Tag: GameEntry game } && game.HasCoverArt)
        {
            cover.Source = _coverArtCache.GetOrCreate(game.Image);
            return;
        }

        cover.Source = null;
    }

    /// <summary>Detach from the Image only — bitmap stays in the LRU for fast revisit.</summary>
    private void GamesGrid_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (FindCoverImage(args.Element) is Image cover)
            cover.Source = null;
    }

    private static Image? FindCoverImage(UIElement element) =>
        FindNamedImage(element, "CoverImage");

    private static Image? FindNamedImage(DependencyObject root, string name)
    {
        if (root is Image image &&
            root is FrameworkElement named &&
            string.Equals(named.Name, name, StringComparison.Ordinal))
        {
            return image;
        }

        if (root is FrameworkElement element && element.FindName(name) is Image found)
            return found;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (FindNamedImage(VisualTreeHelper.GetChild(root, i), name) is Image nested)
                return nested;
        }

        return null;
    }

    private void LibraryScroll_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateCoverArtHeight(LibraryScroll.ViewportWidth > 0 ? LibraryScroll.ViewportWidth : e.NewSize.Width);

    private void ListDetailPane_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateListDetailBannerSize(ListDetailHeroHost?.ActualWidth ?? 0);

    private void ListDetailHeroHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0)
            UpdateListDetailBannerSize(e.NewSize.Width);
    }

    private void UpdateListDetailBannerSize(double width)
    {
        if (ListDetailHeroHost is null || width <= 0)
            return;

        // Steam library background is 3840x1240, shown as a large cover at the
        // top of the details page (~40% of the pane). Wider than that native
        // ratio crops the sides; taller crops top/bottom around the safe area.
        const double steamAspect = 1240.0 / 3840.0;
        double aspectHeight = Math.Round(width * steamAspect);
        double paneHeight = ListDetailPane?.ActualHeight ?? 0;
        double height = aspectHeight;
        if (paneHeight > 200)
            height = Math.Min(Math.Max(aspectHeight, Math.Round(paneHeight * 0.40)), Math.Round(paneHeight * 0.48));

        if (Math.Abs(ListDetailHeroHost.Height - height) > 0.5)
            ListDetailHeroHost.Height = height;

        if (ListDetailBody is null)
            return;

        const double overlap = 80;
        double top = Math.Max(96, ListDetailHeroHost.Height - overlap);
        ListDetailBody.Margin = new Thickness(0, top, 0, 0);
    }

    private void UpdateCoverArtHeight(double viewportWidth)
    {
        // Prefer the real content width (excludes scrollbar) so Fill columns don't overflow.
        double width = LibraryScroll.ViewportWidth;
        if (width <= 0)
            width = GamesGrid.ActualWidth;
        if (width <= 0)
            width = viewportWidth > 16 ? viewportWidth - 16 : viewportWidth;

        if (width <= 0)
            return;

        // Match GamesGrid right margin so column math matches UniformGridLayout.
        width = Math.Max(1, width - 16);

        const double minItemWidth = 180;
        const double gap = 10;
        int columns = Math.Max(1, (int)((width + gap) / (minItemWidth + gap)));
        double itemWidth = (width - gap * (columns - 1)) / columns;
        // Cap cover height so a single row can't dominate the viewport and clip buttons.
        double target = Math.Min(itemWidth * (450.0 / 300.0), Math.Max(180, LibraryScroll.ViewportHeight * 0.55));
        if (LibraryScroll.ViewportHeight <= 0)
            target = itemWidth * (450.0 / 300.0);

        if (Math.Abs(CoverArtHeight - target) > 0.5)
        {
            CoverArtHeight = target;
            // Cover height feeds item size — force repeater to remeasure scroll extent.
            GamesGrid.InvalidateMeasure();
            GamesGrid.InvalidateArrange();
        }
    }

    private void HandleRefresh()
    {
        _refreshPending = true;
        // Refresh can be requested from a background download task — never touch XAML off-UI.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded)
                _ = EnsureLoadedAsync(force: true);
        });
    }

    private void EnsureDownloadsHook()
    {
        if (_downloadsHooked)
            return;

        _downloadsPage = _services.GetRequiredService<DownloadsPage>();
        _downloadsPage.InstallingChanged += OnInstallingChanged;
        _downloadsHooked = true;
    }

    private void OnInstallingChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
            SyncInstallingFlags();
        else
            DispatcherQueue.TryEnqueue(SyncInstallingFlags);
    }

    private void SyncInstallingFlags()
    {
        DownloadsPage? downloads = _downloadsPage;
        if (downloads is null)
            return;

        foreach (GameEntry game in AppsList)
            game.IsInstalling = downloads.IsAppInstalling(game.AppId);

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedGameStatus)));
    }

    private Task EnsureLoadedAsync(bool force = false)
    {
        if (!force && _hasLoaded && !_refreshPending)
            return Task.CompletedTask;

        // Coalesce overlapping loads unless a forced refresh needs a newer snapshot.
        if (_loadTask is { IsCompleted: false } && !force)
            return _loadTask;

        _loadTask = LoadAppsAsync();
        return _loadTask;
    }

    private async Task LoadAppsAsync()
    {
        int version = Interlocked.Increment(ref _loadVersion);

        List<GameEntry> games;
        try
        {
            games = await _gameLibrary.LoadAsync().ConfigureAwait(false);
            games = games
                .OrderByDescending(game => game.IsInstalled)
                .ThenBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return;
        }

        if (version != _loadVersion)
            return;

        void Apply()
        {
            if (version != _loadVersion)
                return;

            SyncApps(games);
            ApplyFilter();
            _hasLoaded = true;
            _refreshPending = false;
            EnsureDownloadsHook();
            SyncInstallingFlags();
            RefreshRunningGames();
            _ = FillInstallSizesAsync(version);
            _ = FillListIconsAsync(version);
        }

        if (DispatcherQueue.HasThreadAccess)
            Apply();
        else
        {
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        Apply();
                        applied.SetResult();
                    }
                    catch (Exception ex)
                    {
                        applied.SetException(ex);
                    }
                }))
            {
                return;
            }

            await applied.Task.ConfigureAwait(false);
        }
    }

    private void SyncApps(IReadOnlyList<GameEntry> games)
    {
        if (CollectionsEqual(AppsList, games))
            return;

        ClearSelection();
        AppsList.Clear();
        foreach (var game in games)
            AppsList.Add(game);
    }

    public void ApplySearchFilter(string? query)
    {
        _searchQuery = query ?? string.Empty;
        ApplyFilter();
    }

    public void SetShowDownloadedOnly(bool showDownloadedOnly)
    {
        if (_showDownloadedOnly == showDownloadedOnly)
            return;

        _showDownloadedOnly = showDownloadedOnly;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<GameEntry> source = AppsList;

        if (_showDownloadedOnly)
            source = source.Where(game => game.IsInstalled);

        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            source = source.Where(game =>
                game.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                game.AppId.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = source.ToList();
        if (CollectionsEqual(FilteredApps, filtered))
            return;

        ClearSelection();
        FilteredApps.Clear();
        foreach (var game in filtered)
            FilteredApps.Add(game);

        if (_useListView)
            SyncListSelection();
    }

    public void SetLibraryListView(bool useListView)
    {
        _useListView = useListView;
        GridViewHost.Visibility = useListView ? Visibility.Collapsed : Visibility.Visible;
        ListViewHost.Visibility = useListView ? Visibility.Visible : Visibility.Collapsed;
        if (useListView)
        {
            SyncListSelection();
            RestoreVisibleMedia();
            if (_hasLoaded)
                _ = FillListIconsAsync(_loadVersion);
        }
        else
        {
            CloseTheaterMode();
        }
    }

    private void SetSelectedGame(GameEntry? game)
    {
        if (ReferenceEquals(_selectedGame, game))
            return;

        _selectedGame = game;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedGame)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedGameStatus)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelectedGameVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoSelectedGameVisibility)));
        _ = LoadListDetailArtworkAsync();
    }

    private void SyncListSelection()
    {
        GameEntry? keep = null;
        if (_selectedGame is not null)
        {
            keep = FilteredApps.FirstOrDefault(game =>
                string.Equals(game.AppId, _selectedGame.AppId, StringComparison.OrdinalIgnoreCase));
        }

        keep ??= FilteredApps.FirstOrDefault();
        SetSelectedGame(keep);
        ApplyListViewSelectionFromIds(keep);
    }

    private async Task LoadListDetailArtworkAsync()
    {
        int version = Interlocked.Increment(ref _heroLoadVersion);
        GameEntry? game = _selectedGame;

        if (ListDetailCover is null)
            return;

        ResetGameMediaPreview();
        ListDetailCover.Source = null;
        if (ListDetailLogo is not null)
        {
            ListDetailLogo.Source = null;
            ListDetailLogo.Visibility = Visibility.Collapsed;
        }

        if (ListDetailTitle is not null)
            ListDetailTitle.Visibility = Visibility.Visible;

        if (game is null)
            return;

        _ = EnsureStoreDetailsAsync(game, version);
        ApplyListDetailLogo(game);

        string? heroPath = SteamMetadataService.ResolveHeroPath(game.Image);
        if (string.IsNullOrWhiteSpace(heroPath) && !string.IsNullOrWhiteSpace(game.AppId))
            heroPath = Path.Combine(AppPaths.ManifestsDirectory, $"undefined_{game.AppId}", "Assets", "LibraryHero.jpg");

        if (!string.IsNullOrWhiteSpace(heroPath)
            && (!File.Exists(heroPath) || new FileInfo(heroPath).Length == 0)
            && !string.IsNullOrWhiteSpace(game.AppId))
        {
            try
            {
                await _steamMetadata.DownloadHeroAsync(game.AppId, heroPath);
            }
            catch (Exception ex)
            {
                AppLog.Write(ex, $"[Library] Hero download failed for appId={game.AppId}");
            }
        }

        if (version != _heroLoadVersion)
            return;

        if (!string.IsNullOrWhiteSpace(heroPath) && File.Exists(heroPath) && new FileInfo(heroPath).Length > 0)
            SetListDetailBanner(heroPath);
    }

    private async Task EnsureStoreDetailsAsync(GameEntry game, int version)
    {
        if (game.MediaLoaded)
            ShowFirstMedia(game);

        bool needAbout = string.IsNullOrWhiteSpace(game.AboutTheGame) && !game.AboutTheGameLoaded;
        bool needMedia = !game.MediaLoaded;
        if (!needAbout && !needMedia)
            return;

        if (string.IsNullOrWhiteSpace(game.AppId))
        {
            game.AboutTheGameLoaded = true;
            game.SetMedia([]);
            return;
        }

        try
        {
            SteamStorePageInfo info = await _steamMetadata.GetStorePageInfoAsync(game.AppId);
            if (version != _heroLoadVersion)
                return;

            if (needAbout)
            {
                game.AboutTheGameLoaded = true;
                if (!string.IsNullOrWhiteSpace(info.AboutTheGame))
                {
                    game.AboutTheGame = info.AboutTheGame;
                    await _gameLibrary.SaveAsync(AppsList);
                }
            }

            if (needMedia)
            {
                game.SetMedia(info.Media);
                ShowFirstMedia(game);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"[Library] Store details failed for appId={game.AppId}");
        }
    }

    private void ApplyListDetailLogo(GameEntry game)
    {
        string? directory = Path.GetDirectoryName(game.Image);
        if (string.IsNullOrWhiteSpace(directory) || ListDetailLogo is null)
            return;

        string logoPath = Path.Combine(directory, "GameLogo.png");
        if (!File.Exists(logoPath))
            return;

        try
        {
            ListDetailLogo.Source = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = 420,
                UriSource = new Uri(Path.GetFullPath(logoPath), UriKind.Absolute)
            };
            ListDetailLogo.Visibility = Visibility.Visible;
            if (ListDetailTitle is not null)
                ListDetailTitle.Visibility = Visibility.Collapsed;
        }
        catch
        {
        }
    }

    private void SetListDetailBanner(string path)
    {
        if (ListDetailCover is null)
            return;

        try
        {
            ListDetailCover.Source = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = 1920,
                UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute)
            };
        }
        catch
        {
        }
    }

    private void ShowFirstMedia(GameEntry game)
    {
        CloseTheaterMode();
        if (game.MediaItems.Count == 0)
        {
            UpdateMediaTiles(null);
            return;
        }

        _mediaIndex = 0;
        _mediaPageStart = Math.Clamp(_mediaPageStart, 0, Math.Max(0, game.MediaItems.Count - 1));
        _mediaPageStart -= _mediaPageStart % 3;
        UpdateMediaTiles(game);
    }

    private void RestoreVisibleMedia()
    {
        if (!_useListView || _selectedGame is not { MediaLoaded: true, MediaItems.Count: > 0 })
            return;

        UpdateMediaTiles(_selectedGame);
    }

    private void UpdateMediaTiles(GameEntry? game)
    {
        Image[] tiles = [MediaTile0, MediaTile1, MediaTile2];
        UIElement[] hosts = [MediaTileHost0, MediaTileHost1, MediaTileHost2];
        UIElement[] badges = [MediaTile0Badge, MediaTile1Badge, MediaTile2Badge];
        UIElement[] selects = [MediaTile0Select, MediaTile1Select, MediaTile2Select];
        int count = game?.MediaItems.Count ?? 0;

        for (int slot = 0; slot < 3; slot++)
        {
            int index = _mediaPageStart + slot;
            GameMediaItem? item = game is not null && index < count
                ? game.MediaItems[index]
                : null;
            bool show = item is not null;

            if (hosts[slot] is not null)
                hosts[slot].Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            if (MediaViewportHost is not null && slot < MediaViewportHost.ColumnDefinitions.Count)
                MediaViewportHost.ColumnDefinitions[slot].Width = new GridLength(1, GridUnitType.Star);

            if (tiles[slot] is not null)
            {
                tiles[slot].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                Uri? uri = item?.ImageUri ?? item?.ThumbnailUri;
                tiles[slot].Source = uri is null
                    ? null
                    : new BitmapImage
                    {
                        DecodePixelType = DecodePixelType.Logical,
                        DecodePixelWidth = 640,
                        UriSource = uri
                    };
            }

            if (badges[slot] is not null)
                badges[slot].Visibility = item is { IsVideo: true } ? Visibility.Visible : Visibility.Collapsed;
            if (selects[slot] is not null)
                selects[slot].Visibility = Visibility.Collapsed;
        }

        Visibility arrows = count > 3 ? Visibility.Visible : Visibility.Collapsed;
        if (GameMediaPrevButton is not null)
            GameMediaPrevButton.Visibility = arrows;
        if (GameMediaNextButton is not null)
        {
            GameMediaNextButton.Visibility = arrows;
            Grid.SetColumn(GameMediaNextButton, 2);
        }
    }

    private void ResetGameMediaPreview()
    {
        _mediaIndex = 0;
        _mediaPageStart = 0;
        CloseTheaterMode();
        UpdateMediaTiles(null);
    }

    private void StopTheaterPlayback()
    {
        Interlocked.Increment(ref _theaterPlayVersion);
        _theaterItem = null;
        if (TheaterPlayer is null)
            return;

        try
        {
            TheaterPlayer.MediaPlayer?.Pause();
        }
        catch
        {
        }

        TheaterPlayer.Source = null;
        TheaterPlayer.PosterSource = null;
        TheaterPlayer.Visibility = Visibility.Collapsed;
    }

    private void MediaTile_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_selectedGame is null || sender is not FrameworkElement element)
            return;

        if (!int.TryParse(Convert.ToString(element.Tag), out int slot))
            return;

        int index = _mediaPageStart + slot;
        if (index < 0 || index >= _selectedGame.MediaItems.Count)
            return;

        e.Handled = true;
        OpenTheaterMode(index);
    }

    private void GameMediaPrev_Click(object sender, RoutedEventArgs e) =>
        ShiftMediaPage(-3);

    private void GameMediaNext_Click(object sender, RoutedEventArgs e) =>
        ShiftMediaPage(3);

    private void ShiftMediaPage(int delta)
    {
        if (_selectedGame is null || _selectedGame.MediaItems.Count == 0)
            return;

        int count = _selectedGame.MediaItems.Count;
        int pageCount = Math.Max(1, (int)Math.Ceiling(count / 3.0));
        int page = _mediaPageStart / 3;
        page = (page + Math.Sign(delta) + pageCount) % pageCount;
        _mediaPageStart = page * 3;
        UpdateMediaTiles(_selectedGame);
    }

    private void OpenTheaterMode(int index)
    {
        if (_selectedGame is null || _selectedGame.MediaItems.Count == 0 || TheaterOverlay is null)
            return;

        _mediaIndex = Math.Clamp(index, 0, _selectedGame.MediaItems.Count - 1);
        TheaterOverlay.Visibility = Visibility.Visible;
        ShowTheaterItem(_selectedGame.MediaItems[_mediaIndex]);
    }

    private void ShowTheaterItem(GameMediaItem item)
    {
        StopTheaterPlayback();
        IReadOnlyList<Uri> videos = item.VideoUris.Count > 0
            ? item.VideoUris
            : item.VideoUri is null ? [] : [item.VideoUri];
        if (item.IsVideo && videos.Count > 0 && TheaterPlayer is not null)
        {
            if (TheaterImage is not null)
                TheaterImage.Visibility = Visibility.Collapsed;
            TheaterPlayer.Visibility = Visibility.Visible;
            TheaterPlayer.PosterSource = item.ThumbnailUri is null
                ? null
                : new BitmapImage { UriSource = item.ThumbnailUri };
            _theaterItem = item;
            _ = PlayTheaterSourceAsync(item, videos, 0);
            return;
        }

        ShowTheaterImage(item);
    }

    private async Task PlayTheaterSourceAsync(GameMediaItem item, IReadOnlyList<Uri> videos, int index)
    {
        int version = Interlocked.Increment(ref _theaterPlayVersion);
        if (TheaterPlayer is null)
            return;

        if (index < 0 || index >= videos.Count)
        {
            if (version == _theaterPlayVersion)
                ShowTheaterImage(item);
            return;
        }

        HookTheaterMediaFailed();
        _theaterSourceIndex = index;
        Uri uri = videos[index];
        try
        {
            IMediaPlaybackSource? source = await CreateTheaterSourceAsync(uri);
            if (version != _theaterPlayVersion)
                return;

            if (source is not null)
            {
                TheaterPlayer.Source = source;
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"[Library] Trailer source failed: {uri}");
        }

        if (version == _theaterPlayVersion)
            await PlayTheaterSourceAsync(item, videos, index + 1);
    }

    private static async Task<IMediaPlaybackSource?> CreateTheaterSourceAsync(Uri uri)
    {
        if (IsAdaptiveManifest(uri))
        {
            AdaptiveMediaSourceCreationResult result = await AdaptiveMediaSource.CreateFromUriAsync(
                uri,
                SteamVideoHttp.Value);
            if (result.Status == AdaptiveMediaSourceCreationStatus.Success)
                return MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);

            AppLog.Write($"[Library] Adaptive trailer rejected ({result.Status}): {uri}");
            return null;
        }

        return MediaSource.CreateFromUri(uri);
    }

    private void HookTheaterMediaFailed()
    {
        if (TheaterPlayer is null || _theaterMediaFailedHooked)
            return;

        if (TheaterPlayer.MediaPlayer is null)
            TheaterPlayer.SetMediaPlayer(new MediaPlayer { AutoPlay = true });

        if (TheaterPlayer.MediaPlayer is null)
            return;

        TheaterPlayer.MediaPlayer.MediaFailed += OnTheaterMediaFailed;
        _theaterMediaFailedHooked = true;
    }

    private void OnTheaterMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        GameMediaItem? item = _theaterItem;
        if (item is null)
            return;

        IReadOnlyList<Uri> videos = item.VideoUris.Count > 0
            ? item.VideoUris
            : item.VideoUri is null ? [] : [item.VideoUri];
        int next = _theaterSourceIndex + 1;
        int version = _theaterPlayVersion;
        string failed = next - 1 >= 0 && next - 1 < videos.Count
            ? videos[next - 1].ToString()
            : string.Empty;
        AppLog.Write($"[Library] Trailer playback failed ({args.Error}): {args.ErrorMessage} {failed}");

        DispatcherQueue.TryEnqueue(() =>
        {
            if (version != _theaterPlayVersion || !ReferenceEquals(item, _theaterItem))
                return;

            _ = PlayTheaterSourceAsync(item, videos, next);
        });
    }

    private static bool IsAdaptiveManifest(Uri uri)
    {
        string path = uri.AbsolutePath;
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
    }

    private static SteamVideoHttpClient CreateSteamVideoHttp()
    {
        var client = new SteamVideoHttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Referer = new Uri("https://store.steampowered.com/");
        return client;
    }

    private void ShowTheaterImage(GameMediaItem item)
    {
        StopTheaterPlayback();
        if (TheaterImage is null)
            return;

        TheaterImage.Visibility = Visibility.Visible;
        Uri? uri = item.ImageUri ?? item.ThumbnailUri;
        TheaterImage.Source = uri is null
            ? null
            : new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = 1600,
                UriSource = uri
            };
    }

    private void CloseTheaterMode()
    {
        StopTheaterPlayback();
        if (TheaterImage is not null)
            TheaterImage.Source = null;
        if (TheaterOverlay is not null)
            TheaterOverlay.Visibility = Visibility.Collapsed;
    }

    private void TheaterBackdrop_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        CloseTheaterMode();
    }

    private void TheaterContent_Tapped(object sender, TappedRoutedEventArgs e) =>
        e.Handled = true;

    private void TheaterClose_Click(object sender, RoutedEventArgs e) =>
        CloseTheaterMode();

    private void TheaterPrev_Click(object sender, RoutedEventArgs e) =>
        ShiftTheater(-1);

    private void TheaterNext_Click(object sender, RoutedEventArgs e) =>
        ShiftTheater(1);

    private void ShiftTheater(int delta)
    {
        if (_selectedGame is null || _selectedGame.MediaItems.Count == 0)
            return;

        int count = _selectedGame.MediaItems.Count;
        _mediaIndex = (_mediaIndex + delta + count) % count;
        ShowTheaterItem(_selectedGame.MediaItems[_mediaIndex]);
    }

    private void MediaViewportHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (MediaViewportHost is null)
            return;

        double width = MediaViewportHost.ActualWidth;
        if (width <= 0)
            return;

        const double gap = 8;
        const int columns = 3;
        const double maxHeight = 280;
        double tileWidth = Math.Max(1, (width - gap * (columns - 1)) / columns);
        double naturalHeight = tileWidth * 9.0 / 16.0;
        double height = Math.Clamp(Math.Round(naturalHeight), 120, maxHeight);
        double maxWidth = maxHeight * 16.0 / 9.0 * columns + gap * (columns - 1);
        if (Math.Abs(MediaViewportHost.MaxWidth - maxWidth) > 0.5)
            MediaViewportHost.MaxWidth = maxWidth;
        if (Math.Abs(MediaViewportHost.Height - height) > 0.5)
            MediaViewportHost.Height = height;
    }

    private void GamesList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not GameEntry game)
            return;

        if (FindNamedImage(args.ItemContainer, "ListCoverImage") is not Image cover)
            return;

        ApplyListIcon(cover, game);
    }

    private void ApplyListIcon(Image image, GameEntry game)
    {
        if (game.HasIcon && !string.IsNullOrWhiteSpace(game.ResolvedIconPath))
        {
            image.Stretch = Stretch.Uniform;
            image.Source = _coverArtCache.GetOrCreate(game.ResolvedIconPath, 64);
            return;
        }

        if (game.HasCoverArt)
        {
            image.Stretch = Stretch.UniformToFill;
            image.Source = _coverArtCache.GetOrCreate(game.Image);
            return;
        }

        image.Source = null;
    }

    private void RefreshListIcon(GameEntry game)
    {
        if (GamesList.ContainerFromItem(game) is not DependencyObject container)
            return;

        if (FindNamedImage(container, "ListCoverImage") is Image image)
            ApplyListIcon(image, game);
    }

    private async Task FillListIconsAsync(int version)
    {
        var missing = AppsList
            .Where(game => !game.HasIcon && !string.IsNullOrWhiteSpace(game.AppId))
            .ToList();
        if (missing.Count == 0)
            return;

        await Parallel.ForEachAsync(
            missing,
            new ParallelOptions { MaxDegreeOfParallelism = 6 },
            async (game, cancellationToken) =>
            {
                if (version != _loadVersion)
                    return;

                string? iconPath = game.ResolvedIconPath;
                if (string.IsNullOrWhiteSpace(iconPath) && !string.IsNullOrWhiteSpace(game.AppId))
                    iconPath = Path.Combine(AppPaths.ManifestsDirectory, $"undefined_{game.AppId}", "Assets", "GameIcon.jpg");
                if (string.IsNullOrWhiteSpace(iconPath))
                    return;

                try
                {
                    bool downloaded = await _steamMetadata
                        .DownloadIconAsync(game.AppId, iconPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (!downloaded || version != _loadVersion)
                        return;

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (version != _loadVersion)
                            return;

                        game.RefreshArtworkFlags();
                        RefreshListIcon(game);
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Write(ex, $"[Library] Icon download failed for appId={game.AppId}");
                }
            }).ConfigureAwait(false);
    }

    private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_useListView || _suppressListSelectionChanged)
            return;

        var selected = GamesList.SelectedItems.OfType<GameEntry>().ToList();
        var ids = new HashSet<string>(selected.Select(game => game.AppId), StringComparer.OrdinalIgnoreCase);

        foreach (var game in AppsList)
            SetSelected(game, ids.Contains(game.AppId));
        foreach (var game in FilteredApps)
            SetSelected(game, ids.Contains(game.AppId));

        if (GamesList.SelectedItem is GameEntry focused)
        {
            SetSelectedGame(focused);
            _selectionAnchorIndex = IndexOfFiltered(focused);
            return;
        }

        if (selected.Count > 0)
        {
            SetSelectedGame(selected[^1]);
            _selectionAnchorIndex = IndexOfFiltered(selected[^1]);
            return;
        }

        SetSelectedGame(null);
    }

    private async void GamesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (GetGameEntry(e.OriginalSource) is not GameEntry game)
            return;

        e.Handled = true;
        SelectOnlyListGame(game);
        await RunPrimaryActionAsync(game);
    }

    private void GamesList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (GetGameEntry(e.OriginalSource) is not GameEntry game)
            return;

        if (!_selectedAppIds.Contains(game.AppId))
            SelectOnlyListGame(game);

        SetSelectedGame(game);
        Card_RightTapped(e.OriginalSource, e);
        e.Handled = true;
    }

    private void SelectOnlyListGame(GameEntry game)
    {
        ClearSelection();
        SetSelected(game, true);
        _selectionAnchorIndex = IndexOfFiltered(game);
        SetSelectedGame(game);
        ApplyListViewSelectionFromIds(game);
    }

    private void ApplyListViewSelectionFromIds(GameEntry? focus)
    {
        if (GamesList is null)
            return;

        _suppressListSelectionChanged = true;
        try
        {
            GamesList.SelectedItems.Clear();
            var chosen = FilteredApps
                .Where(game => _selectedAppIds.Contains(game.AppId))
                .ToList();

            if (chosen.Count == 0 && focus is not null)
            {
                GamesList.SelectedItem = focus;
                return;
            }

            foreach (var game in chosen)
                GamesList.SelectedItems.Add(game);

            if (focus is not null && chosen.Any(game =>
                    string.Equals(game.AppId, focus.AppId, StringComparison.OrdinalIgnoreCase)))
                GamesList.SelectedItem = focus;
        }
        finally
        {
            _suppressListSelectionChanged = false;
        }
    }

    private async void ListDetailPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is null)
            return;

        await RunPrimaryActionAsync(_selectedGame);
    }

    private void ListDetailStop_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is not null)
            StopGame(_selectedGame);
    }

    private void ListDetailManage_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is null)
            return;

        ShowGameContextMenu(_selectedGame, ListDetailManageButton, new Windows.Foundation.Point(0, ListDetailManageButton.ActualHeight));
    }

    private void ListDetailStore_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is not null)
            OpenStorePage(_selectedGame);
    }

    private static bool CollectionsEqual(IReadOnlyList<GameEntry> left, IReadOnlyList<GameEntry> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!EntriesEqual(left[i], right[i]))
                return false;
        }

        return true;
    }

    private async Task FillInstallSizesAsync(int version)
    {
        var missing = AppsList
            .Where(game => game.InstallSizeBytes is not > 0)
            .ToList();
        if (missing.Count == 0)
            return;

        var updates = new System.Collections.Concurrent.ConcurrentBag<(GameEntry Game, long Size)>();
        await Parallel.ForEachAsync(
            missing,
            new ParallelOptions { MaxDegreeOfParallelism = 6 },
            async (game, cancellationToken) =>
            {
                if (version != _loadVersion)
                    return;

                try
                {
                    long? size = await _installSizeService
                        .ResolveAsync(game, cancellationToken)
                        .ConfigureAwait(false);
                    if (size is > 0 && game.InstallSizeBytes != size)
                    {
                        updates.Add((game, size.Value));
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (version == _loadVersion)
                                game.InstallSizeBytes = size;
                        });
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write(ex, $"Install size failed for appId={game.AppId}");
                }
            }).ConfigureAwait(false);

        if (updates.IsEmpty || version != _loadVersion)
            return;

        var snapshot = updates.ToList();
        void ApplySizes()
        {
            foreach (var (game, size) in snapshot)
                game.InstallSizeBytes = size;
        }

        if (DispatcherQueue.HasThreadAccess)
            ApplySizes();
        else
        {
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        ApplySizes();
                        applied.SetResult();
                    }
                    catch (Exception ex)
                    {
                        applied.SetException(ex);
                    }
                }))
            {
                return;
            }

            await applied.Task.ConfigureAwait(false);
        }

        if (version != _loadVersion)
            return;

        try
        {
            await _gameLibrary.SaveAsync(AppsList).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Failed to save install sizes");
        }
    }

    private static bool EntriesEqual(GameEntry left, GameEntry right) =>
        string.Equals(left.AppId, right.AppId, StringComparison.Ordinal) &&
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        string.Equals(left.Image, right.Image, StringComparison.Ordinal) &&
        string.Equals(left.StartLocation, right.StartLocation, StringComparison.Ordinal) &&
        string.Equals(left.LaunchOptions, right.LaunchOptions, StringComparison.Ordinal) &&
        string.Equals(left.InstallPath, right.InstallPath, StringComparison.Ordinal) &&
        left.IsInstalled == right.IsInstalled;

    private async void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetGameEntry(sender) is not GameEntry game)
            return;

        await RunPrimaryActionAsync(game);
    }

    private void StopGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetGameEntry(sender) is GameEntry game)
            StopGame(game);
    }

    private async void Card_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsFromActionButton(e.OriginalSource as DependencyObject))
            return;

        if (GetGameEntry(sender) is not GameEntry game)
            return;

        e.Handled = true;
        ClearSelection();
        SetSelected(game, true);
        _selectionAnchorIndex = IndexOfFiltered(game);
        await RunPrimaryActionAsync(game);
    }

    private async Task RunPrimaryActionAsync(GameEntry game)
    {
        if (game.IsInstalling)
            return;

        if (!game.IsInstalled)
        {
            await InstallGameAsync(game);
            return;
        }

        await PlayGameAsync(game);
    }

    private void Card_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
            return;

        if (IsFromActionButton(e.OriginalSource as DependencyObject))
            return;

        if (GetGameEntry(sender) is not GameEntry game)
            return;

        bool ctrl = IsModifierDown(VirtualKey.Control);
        bool shift = IsModifierDown(VirtualKey.Shift);
        ApplyCardSelection(game, ctrl, shift);
    }

    private void Card_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (GetGameEntry(sender) is not GameEntry game)
            return;

        if (!_selectedAppIds.Contains(game.AppId))
        {
            ClearSelection();
            SetSelected(game, true);
            _selectionAnchorIndex = IndexOfFiltered(game);
        }

        ShowGameContextMenu(game, sender as FrameworkElement, e.GetPosition(sender as UIElement));
        e.Handled = true;
    }

    private void ShowGameContextMenu(GameEntry game, FrameworkElement? target, Windows.Foundation.Point position)
    {
        var flyout = new MenuFlyout();

        if (_selectedAppIds.Count > 1)
        {
            var removeCards = new MenuFlyoutItem
            {
                Text = $"Remove {_selectedAppIds.Count} cards",
                Tag = game
            };
            removeCards.Click += RemoveSelectedCardsMenuItem_Click;
            flyout.Items.Add(removeCards);
        }
        else
        {
            bool isInstalled = game.IsInstalled;
            flyout.Items.Add(CreateMenuItem("Open install location", game, OpenInstallLocationMenuItem_Click, isInstalled));
            flyout.Items.Add(CreateMenuItem("Create desktop shortcut", game, CreateDesktopShortcutMenuItem_Click, isInstalled));
            flyout.Items.Add(CreateMenuItem("Change default executable", game, ChangeDefaultExecutableMenuItem_Click, isInstalled));
            flyout.Items.Add(CreateMenuItem("Custom launch options", game, CustomLaunchOptionsMenuItem_Click, isInstalled));
            flyout.Items.Add(CreateMenuItem("Remove Steam DRM", game, RemoveSteamDrmMenuItem_Click, isInstalled));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(CreateMenuItem("Visit store page", game, VisitStorePageMenuItem_Click));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(CreateMenuItem("Uninstall", game, UninstallMenuItem_Click));
        }

        if (target is not null)
            flyout.ShowAt(target, position);
    }

    private static MenuFlyoutItem CreateMenuItem(string text, GameEntry game, RoutedEventHandler click, bool isEnabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, Tag = game, IsEnabled = isEnabled };
        item.Click += click;
        return item;
    }

    private async void RemoveSelectedCardsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var selected = FilteredApps
            .Where(game => _selectedAppIds.Contains(game.AppId))
            .ToList();

        if (selected.Count == 0)
            return;

        var libraryOnly = selected.Where(game => !game.IsInstalled).ToList();
        var installed = selected.Where(game => game.IsInstalled).ToList();

        if (libraryOnly.Count > 0)
        {
            string message = libraryOnly.Count == 1
                ? $"Remove {libraryOnly[0].Name} from the library?\nThis removes the card and its extracted manifest folder(s). Installed game files are not deleted."
                : $"Remove {libraryOnly.Count} library-only cards?\nThis removes the cards and their extracted manifest folder(s). Installed game files are not deleted.";

            if (installed.Count > 0)
            {
                message += installed.Count == 1
                    ? $"\n\nYou will then be asked about uninstalling {installed[0].Name}."
                    : $"\n\nYou will then be asked about uninstalling {installed.Count} installed games.";
            }

            var result = await _messageBoxService.ShowAsync(
                libraryOnly.Count == 1 ? "Remove from library ?" : "Remove cards ?",
                message,
                "Remove",
                "Cancel");

            if (result != ContentDialogResult.Primary)
                return;

            foreach (var game in libraryOnly)
            {
                _shortcutService.RemoveDesktopShortcut(game.Name);
                await _gameLibrary.RemoveAsync(game.AppId);
            }
        }

        foreach (var game in installed)
            await UninstallInstalledGameAsync(game);

        ClearSelection();
        _refreshPending = true;
        await EnsureLoadedAsync(force: true);
    }

    private void ApplyCardSelection(GameEntry game, bool ctrl, bool shift)
    {
        int index = IndexOfFiltered(game);
        if (index < 0)
            return;

        if (shift && _selectionAnchorIndex >= 0)
        {
            int start = Math.Min(_selectionAnchorIndex, index);
            int end = Math.Max(_selectionAnchorIndex, index);

            if (!ctrl)
                ClearSelection();

            for (int i = start; i <= end; i++)
                SetSelected(FilteredApps[i], true);

            return;
        }

        if (ctrl)
        {
            SetSelected(game, !_selectedAppIds.Contains(game.AppId));
            _selectionAnchorIndex = index;
            return;
        }

        ClearSelection();
        SetSelected(game, true);
        _selectionAnchorIndex = index;
    }

    private void ClearSelection()
    {
        if (_selectedAppIds.Count == 0)
        {
            _selectionAnchorIndex = -1;
            return;
        }

        foreach (var game in AppsList)
            game.IsSelected = false;
        foreach (var game in FilteredApps)
            game.IsSelected = false;

        _selectedAppIds.Clear();
        _selectionAnchorIndex = -1;

        if (_useListView && GamesList is not null)
        {
            _suppressListSelectionChanged = true;
            try
            {
                GamesList.SelectedItems.Clear();
            }
            finally
            {
                _suppressListSelectionChanged = false;
            }
        }
    }

    private void SetSelected(GameEntry game, bool selected)
    {
        if (selected)
        {
            if (_selectedAppIds.Add(game.AppId))
                game.IsSelected = true;
            else
                game.IsSelected = true;
        }
        else
        {
            _selectedAppIds.Remove(game.AppId);
            game.IsSelected = false;
        }
    }

    private int IndexOfFiltered(GameEntry game)
    {
        for (int i = 0; i < FilteredApps.Count; i++)
        {
            if (string.Equals(FilteredApps[i].AppId, game.AppId, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static bool IsModifierDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static bool IsFromActionButton(DependencyObject? source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is Button)
                return true;
            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private async Task InstallGameAsync(GameEntry game)
    {
        _navigation.Navigate("Downloads");
        var downloads = _services.GetRequiredService<DownloadsPage>();
        await downloads.BeginInstallFromLibraryAsync(game);
    }

    private async Task PlayGameAsync(GameEntry game)
    {
        if (game.IsInstalling)
            return;

        string? startLocation = game.StartLocation;
        string gameFolder = await ResolveGameFolderAsync(game);

        if (string.IsNullOrWhiteSpace(startLocation))
        {
            string? picked = await PickGameExecutableAsync(game, gameFolder);
            if (string.IsNullOrWhiteSpace(picked))
                return;

            startLocation = picked;
            game.StartLocation = startLocation;
            if (string.IsNullOrWhiteSpace(game.InstallPath) && !string.IsNullOrWhiteSpace(gameFolder))
                game.InstallPath = gameFolder;
            await _gameLibrary.SaveAsync(AppsList);
        }

        string exePath = Path.GetFullPath(startLocation);
        // Games often resolve assets relative to CWD — always use the folder that contains the exe.
        string? workingDirectory = Path.GetDirectoryName(exePath);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            await _messageBoxService.ShowAsync(
                "Unable to start",
                $"Could not resolve working directory for:\n{exePath}");
            return;
        }

        if (!_launchingExes.Add(exePath))
            return;

        try
        {
            if (IsGameAlreadyRunning(exePath))
            {
                AppLog.Write($"[Library] '{game.Name}' is already running; not starting another instance");
                game.IsRunning = true;
                return;
            }

            // Start the game directly (no cmd). ShellExecute returns immediately for GUI apps.
            await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = workingDirectory,
                    Arguments = game.LaunchOptions ?? string.Empty,
                    UseShellExecute = true
                };
                Process.Start(psi);
            });
            _recentLaunches[exePath] = DateTime.UtcNow;
            game.IsRunning = true;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"Unable to start game '{game.Name}' exe={exePath}");
            await _messageBoxService.ShowAsync(
                "Unable to start",
                $"Could not launch:\n{exePath}\n\n{ex.Message}");
        }
        finally
        {
            _launchingExes.Remove(exePath);
        }
    }

    private bool IsGameAlreadyRunning(string exePath)
    {
        if (_recentLaunches.TryGetValue(exePath, out DateTime launched)
            && DateTime.UtcNow - launched < TimeSpan.FromSeconds(8))
        {
            return true;
        }

        return IsProcessNameRunning(ProcessNameFromPath(exePath));
    }

    private void EnsureRunningWatcher()
    {
        if (_runningTimer is null)
        {
            _runningTimer = DispatcherQueue.CreateTimer();
            _runningTimer.Interval = TimeSpan.FromSeconds(1);
            _runningTimer.Tick += (_, _) => RefreshRunningGames();
        }

        if (!_runningTimer.IsRunning)
            _runningTimer.Start();
    }

    private void RefreshRunningGames()
    {
        foreach (var game in AppsList)
        {
            bool running = IsProcessNameRunning(ProcessNameForGame(game));
            if (game.IsRunning != running)
                game.IsRunning = running;
        }
    }

    private static string? ProcessNameForGame(GameEntry game) =>
        string.IsNullOrWhiteSpace(game.StartLocation)
            ? null
            : ProcessNameFromPath(game.StartLocation);

    private static string? ProcessNameFromPath(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static bool IsProcessNameRunning(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;

        try
        {
            Process[] processes = Process.GetProcessesByName(processName);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void StopGame(GameEntry game)
    {
        string? processName = ProcessNameForGame(game);
        if (string.IsNullOrWhiteSpace(processName))
            return;

        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception)
        {
        }

        if (!string.IsNullOrWhiteSpace(game.StartLocation))
        {
            try
            {
                _recentLaunches.Remove(Path.GetFullPath(game.StartLocation));
            }
            catch (Exception)
            {
                _recentLaunches.Remove(game.StartLocation);
            }
        }

        game.IsRunning = false;
    }

    private async Task<string> ResolveGameFolderAsync(GameEntry game)
    {
        if (!string.IsNullOrWhiteSpace(game.InstallPath))
            return Path.GetFullPath(game.InstallPath);

        try
        {
            string computed = await _installPathService.GetInstallDirectoryAsync(game.Name, game.AppId);
            return Path.GetFullPath(computed);
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<string?> PickGameExecutableAsync(GameEntry game, string gameFolder)
    {
        if (string.IsNullOrWhiteSpace(gameFolder) ||
            !await Task.Run(() => Directory.Exists(gameFolder)))
        {
            await _messageBoxService.ShowAsync(
                "Game not installed",
                $"Could not find the install folder for {game.Name}.");
            return null;
        }

        List<string> executables = await Task.Run(() =>
            Directory.EnumerateFiles(gameFolder, "*.exe", SearchOption.AllDirectories)
                .OrderBy(path => Path.GetRelativePath(gameFolder, path).Count(c => c is '\\' or '/'))
                .ThenBy(path => Path.GetRelativePath(gameFolder, path), StringComparer.OrdinalIgnoreCase)
                .ToList());

        if (executables.Count == 0)
        {
            await _messageBoxService.ShowAsync(
                "No executables found",
                $"No .exe files were found in:\n{gameFolder}");
            return null;
        }

        var listPanel = new StackPanel { Spacing = 4 };
        var choices = new List<CheckBox>();

        void EnforceSingleSelection(CheckBox selected)
        {
            foreach (CheckBox box in choices)
            {
                if (!ReferenceEquals(box, selected))
                    box.IsChecked = false;
            }
        }

        foreach (string exePath in executables)
        {
            string relative = Path.GetRelativePath(gameFolder, exePath);
            var checkBox = new CheckBox
            {
                Content = relative,
                Tag = exePath,
                Margin = new Thickness(0, 2, 0, 2)
            };
            checkBox.Checked += (_, _) => EnforceSingleSelection(checkBox);
            choices.Add(checkBox);
            listPanel.Children.Add(checkBox);
        }

        // Prefer current default, then a likely game binary.
        CheckBox? preferred = null;
        if (!string.IsNullOrWhiteSpace(game.StartLocation))
        {
            preferred = choices.FirstOrDefault(box =>
                string.Equals((string)box.Tag, game.StartLocation, StringComparison.OrdinalIgnoreCase));
        }

        preferred ??= choices.FirstOrDefault(box =>
        {
            string name = Path.GetFileName((string)box.Tag);
            return name.Contains("Shipping", StringComparison.OrdinalIgnoreCase)
                || name.Contains(game.Name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, $"{game.AppId}.exe", StringComparison.OrdinalIgnoreCase);
        }) ?? choices[0];
        preferred.IsChecked = true;

        var scrollViewer = new ScrollViewer
        {
            Content = listPanel,
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(new TextBlock
        {
            Text = "Select the game executable to launch:",
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(scrollViewer);

        var dialog = new ContentDialog
        {
            Title = game.Name,
            Content = root,
            PrimaryButtonText = "Use selected",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
        };
        dialog.Resources["ContentDialogMinWidth"] = 420.0;
        dialog.Resources["ContentDialogMaxWidth"] = 720.0;

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return null;

        CheckBox? selected = choices.FirstOrDefault(box => box.IsChecked == true);
        if (selected?.Tag is not string path)
        {
            await _messageBoxService.ShowAsync(
                "Nothing selected",
                "Choose one executable, then click Use selected.");
            return null;
        }

        return path;
    }

    private async void CustomLaunchOptionsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetGameEntry(sender) is not GameEntry game)
            return;

        var box = new TextBox
        {
            Text = game.LaunchOptions ?? string.Empty,
            PlaceholderText = "-windowed -novid",
            AcceptsReturn = false
        };

        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(new TextBlock
        {
            Text = "These arguments are passed to the game when you press Play.",
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(box);

        var dialog = new ContentDialog
        {
            Title = "Custom launch options",
            Content = root,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
        };
        dialog.Resources["ContentDialogMinWidth"] = 420.0;
        dialog.Resources["ContentDialogMaxWidth"] = 560.0;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        game.LaunchOptions = box.Text?.Trim() ?? string.Empty;
        await _gameLibrary.SaveAsync(AppsList);
        AppLog.Write(
            $"[Library] Launch options for '{game.Name}' appId={game.AppId}: '{game.LaunchOptions}'");
    }

    private async void ChangeDefaultExecutableMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetGameEntry(sender) is not GameEntry game)
            return;

        string gameFolder = await ResolveGameFolderAsync(game);
        string? picked = await PickGameExecutableAsync(game, gameFolder);
        if (string.IsNullOrWhiteSpace(picked))
            return;

        game.StartLocation = picked;
        if (string.IsNullOrWhiteSpace(game.InstallPath) && !string.IsNullOrWhiteSpace(gameFolder))
            game.InstallPath = gameFolder;

        await _gameLibrary.SaveAsync(AppsList);
        AppLog.Write($"[Library] Default exe set for '{game.Name}' appId={game.AppId} → {picked}");

        string displayPath = !string.IsNullOrWhiteSpace(gameFolder)
            ? Path.GetRelativePath(gameFolder, picked)
            : picked;
        await _messageBoxService.ShowAsync(
            "Executable updated",
            $"Default executable for {game.Name}:\n{displayPath}");
    }

    private async void OpenInstallLocationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetGameEntry(sender) is not GameEntry game)
            return;

        string gameFolder = await ResolveGameFolderAsync(game);
        bool folderExists = !string.IsNullOrWhiteSpace(gameFolder) &&
            await Task.Run(() => Directory.Exists(gameFolder));
        if (!folderExists)
        {
            await _messageBoxService.ShowAsync(
                "Folder not found",
                $"Could not find the install folder for {game.Name}.");
            return;
        }

        await Task.Run(() =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{gameFolder}\"",
                UseShellExecute = true
            });
        });
    }

    private async void CreateDesktopShortcutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetGameEntry(sender) is not GameEntry game)
            return;

        string gameFolder = await ResolveGameFolderAsync(game);
        bool folderExists = !string.IsNullOrWhiteSpace(gameFolder) &&
            await Task.Run(() => Directory.Exists(gameFolder));

        if (!folderExists)
        {
            await _messageBoxService.ShowAsync(
                "Game not installed",
                $"Could not find the install folder for {game.Name}.");
            return;
        }

        string? startLocation = game.StartLocation;
        if (string.IsNullOrWhiteSpace(startLocation) || !File.Exists(startLocation))
        {
            string? picked = await PickGameExecutableAsync(game, gameFolder);
            if (string.IsNullOrWhiteSpace(picked))
                return;

            startLocation = picked;
            game.StartLocation = startLocation;
            if (string.IsNullOrWhiteSpace(game.InstallPath) && !string.IsNullOrWhiteSpace(gameFolder))
                game.InstallPath = gameFolder;

            await _gameLibrary.SaveAsync(AppsList);
        }

        try
        {
            string exePath = Path.GetFullPath(startLocation);
            string workingDirectory = Path.GetDirectoryName(exePath) ?? gameFolder;
            string shortcutPath = _shortcutService.CreateDesktopShortcut(
                exePath,
                game.Name,
                workingDirectory,
                game.Name,
                game.LaunchOptions);

            AppLog.Write($"[Library] Desktop shortcut created for '{game.Name}' → {shortcutPath}");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"Failed to create desktop shortcut for '{game.Name}'");
            await _messageBoxService.ShowAsync("Failed to create shortcut", ex.Message);
        }
    }

    private async void RemoveSteamDrmMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetGameEntry(sender) is not GameEntry game)
            return;

        string gameFolder = await ResolveGameFolderAsync(game);
        if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
        {
            await _messageBoxService.ShowAsync(
                "Game not installed",
                $"Could not find the install folder for {game.Name}.");
            return;
        }

        try
        {
            _notifications.Show("Removing Steam DRM", $"Running DRM removal for {game.Name}...", InfoBarSeverity.Informational);
            int exitCode = await _postDownloadService.RunPostDownloadCommandAsync(
                game.Name,
                game.AppId,
                gameFolder);

            if (exitCode == 0)
            {
                await _messageBoxService.ShowAsync(
                    "DRM Removal Complete",
                    $"Steam DRM removal completed successfully for {game.Name}.");
            }
            else
            {
                await _messageBoxService.ShowAsync(
                    "DRM Removal Finished",
                    $"Steam DRM removal finished with exit code {exitCode} for {game.Name}.\nCheck the Debug Console for detailed logs.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"DRM removal failed for '{game.Name}' appId={game.AppId}");
            await _messageBoxService.ShowAsync("DRM Removal Failed", ex.Message);
        }
    }

    private void VisitStorePageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetGameEntry(sender) is GameEntry game)
            OpenStorePage(game);
    }

    private void OpenStorePage(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.AppId))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"https://store.steampowered.com/app/{game.AppId}",
                UseShellExecute = true
            });
            AppLog.Write($"[Library] Opening Steam store page for '{game.Name}' appId={game.AppId}");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"Failed to open Steam store page for '{game.Name}' appId={game.AppId}");
        }
    }

    private async void UninstallMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetGameEntry(sender) is not GameEntry game)
            return;

        if (!game.IsInstalled)
        {
            var removeResult = await _messageBoxService.ShowAsync(
                "Remove from library ?",
                $"This removes the card and its extracted manifest folder(s).\nInstalled game files are not deleted.",
                "Remove",
                "Cancel");

            if (removeResult != ContentDialogResult.Primary)
                return;

            _shortcutService.RemoveDesktopShortcut(game.Name);
            await _gameLibrary.RemoveAsync(game.AppId);
            _refreshPending = true;
            await EnsureLoadedAsync(force: true);
            return;
        }

        await UninstallInstalledGameAsync(game);
        _refreshPending = true;
        await EnsureLoadedAsync(force: true);
    }

    /// <summary>
    /// Usual uninstall flow for an installed title. Deletes game files and keeps the card in the library.
    /// </summary>
    private async Task UninstallInstalledGameAsync(GameEntry game)
    {
        var result = await _messageBoxService.ShowAsync(
            "Uninstall game ?",
            $"Are you sure you want to uninstall {game.Name} ?\nThis will delete its installed game files.",
            "Uninstall",
            "Cancel");

        if (result != ContentDialogResult.Primary)
            return;

        try
        {
            await _uninstallService.UninstallAsync(game, removeFromLibrary: false);

            await _gameLibrary.UpsertAsync(new GameEntry
            {
                AppId = game.AppId,
                Name = game.Name,
                Image = game.Image,
                StartLocation = string.Empty,
                InstallPath = string.Empty,
                IsInstalled = false
            });
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"Uninstall failed for '{game.Name}' appId={game.AppId}");
            await _messageBoxService.ShowAsync("Uninstall failed", ex.Message);
        }
    }

    private static GameEntry? GetGameEntry(object sender)
    {
        // ItemsRepeater + x:Bind does not set DataContext; Tag carries the GameEntry.
        if (sender is FrameworkElement { Tag: GameEntry tagged })
            return tagged;

        DependencyObject? current = sender as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement element)
            {
                if (element.Tag is GameEntry tagGame)
                    return tagGame;
                if (element.DataContext is GameEntry game)
                    return game;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
