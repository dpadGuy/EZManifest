using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using EZManifest.Models;
using EZManifest.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using RelayCommand = EZManifest.Commands.RelayCommand;

namespace EZManifest.Views.Pages;

public sealed partial class DownloadsPage : Page
{
    private readonly AppNotificationService _notifications;
    private readonly LuaManifestParser _manifestParser;
    private readonly GameLibraryService _gameLibrary;
    private readonly GameInstallPathService _installPathService;
    private readonly GameUninstallService _uninstallService;
    private readonly ManifestArchiveService _archiveService;
    private readonly SteamMetadataService _steamMetadata;
    private readonly SteamDepotMetadataService _depotMetadata;
    private readonly AppMessageBoxService _messageBoxService;
    private readonly AppSettingsService _settingsService;
    private readonly WindowProvider _windowProvider;
    private readonly PostDownloadService _postDownloadService;
    private readonly WindowsToastService _windowsToast;
    private readonly FileExplorerPickerService _filePicker;

    private string _finalPath = string.Empty;
    private string _appId = string.Empty;
    private string _currentGameName = string.Empty;
    private string _currentCoverArtPath = string.Empty;
    private string _currentLogoPath = string.Empty;
    private bool _dropHighlight;
    private int _dragDepth;
    private int _importBusy;
    private bool _preserveLibraryOnCancel;
    private bool _wasInstalledBeforeDownload;
    private string _installPathBeforeDownload = string.Empty;
    private DispatcherQueueTimer? _elapsedTimer;
    private readonly HashSet<string> _pendingInstallAppIds = new(StringComparer.OrdinalIgnoreCase);
    private const string DepotBoxHomeUrl = "https://depotbox.org/";
    private bool _depotBoxOpen;
    private bool _depotBoxReady;

    public ObservableCollection<DownloadItem> Downloads { get; } = new();
    public event Action? InstallingChanged;

    public DownloadsPage(
        AppNotificationService notifications,
        LuaManifestParser manifestParser,
        GameLibraryService gameLibrary,
        GameInstallPathService installPathService,
        GameUninstallService uninstallService,
        ManifestArchiveService archiveService,
        SteamMetadataService steamMetadata,
        SteamDepotMetadataService depotMetadata,
        AppMessageBoxService messageBoxService,
        AppSettingsService settingsService,
        WindowProvider windowProvider,
        PostDownloadService postDownloadService,
        WindowsToastService windowsToast,
        FileExplorerPickerService filePicker)
    {
        _notifications = notifications;
        _manifestParser = manifestParser;
        _gameLibrary = gameLibrary;
        _installPathService = installPathService;
        _uninstallService = uninstallService;
        _archiveService = archiveService;
        _steamMetadata = steamMetadata;
        _depotMetadata = depotMetadata;
        _messageBoxService = messageBoxService;
        _settingsService = settingsService;
        _windowProvider = windowProvider;
        _postDownloadService = postDownloadService;
        _windowsToast = windowsToast;
        _filePicker = filePicker;

        InitializeComponent();
        Downloads.CollectionChanged += (_, _) =>
        {
            UpdateEmptyState();
            InstallingChanged?.Invoke();
        };
        UpdateEmptyState();
        _settingsService.ManifestSourceSettingsChanged += OnManifestSourceSettingsChanged;
        Unloaded += (_, _) => ReleaseBrowser();
        _ = RefreshManifestSourceButtonsAsync();
    }

    private void OnManifestSourceSettingsChanged() =>
        DispatcherQueue.TryEnqueue(() => _ = RefreshManifestSourceButtonsAsync());

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = RefreshManifestSourceButtonsAsync();
    }

    private async Task RefreshManifestSourceButtonsAsync()
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            bool preferred = settings.UsePreferredManifestSource;
            EmptySourceButton.Content = preferred ? "Use preferred source instead" : "Use DepotBox instead";
            ActiveSourceButton.Content = preferred ? "Preferred source" : "DepotBox";
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Could not refresh manifest source buttons");
        }
    }

    private XamlRoot ResolveDialogXamlRoot()
    {
        if (XamlRoot is not null)
            return XamlRoot;

        if (_windowProvider.Window.Content is FrameworkElement root && root.XamlRoot is not null)
            return root.XamlRoot;

        throw new InvalidOperationException("Main window XamlRoot is not available.");
    }

    private ElementTheme ResolveDialogTheme()
    {
        if (_windowProvider.Window.Content is FrameworkElement root)
            return root.ActualTheme;

        return ActualTheme;
    }

    private void UpdateEmptyState()
    {
        if (_depotBoxOpen)
        {
            DownloadsRoot.Visibility = Visibility.Collapsed;
            DepotBoxPanel.Visibility = Visibility.Visible;
            return;
        }

        bool empty = Downloads.Count == 0;
        DepotBoxPanel.Visibility = Visibility.Collapsed;
        DownloadsRoot.Visibility = Visibility.Visible;
        EmptyDropPrompt.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        BrowseActivePanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void BrowseManifests_Click(object sender, RoutedEventArgs e)
    {
        var kind = await PromptZipOrFolderAsync(
            "Browse manifests",
            "Choose single zip file or folder for import\n" +
            "Choose multiple zip files or folders for batch import");
        if (kind is null)
            return;

        IReadOnlyList<string> paths;
        if (kind == ManifestPickKind.Zip)
        {
            paths = await PickZipFilesAsync() ?? Array.Empty<string>();
        }
        else
        {
            paths = await PickManifestFoldersAsync();
        }

        if (paths.Count == 0)
            return;

        await ImportAndDownloadAsync(paths);
    }

    private async void UseDepotBox_Click(object sender, RoutedEventArgs e)
    {
        (bool preferred, string url) = await _settingsService.GetManifestSourceAsync();
        if (preferred && !AppSettingsService.TryNormalizeHttpUrl(url, out _))
        {
            await _messageBoxService.ShowAsync(
                "Preferred source missing",
                "Turn on Preferred source in Settings and Apply a website address.");
            return;
        }

        _depotBoxOpen = true;
        UpdateEmptyState();
        await EnsureDepotBoxReadyAsync();
        if (_depotBoxReady)
            DepotBoxBrowser.Source = new Uri(url);
        UpdateDepotBoxChrome();
        if (!preferred)
            await ShowDepotBoxGuideIfNeededAsync();
    }

    private async Task ShowDepotBoxGuideIfNeededAsync()
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            if (settings.HasSeenDepotBoxGuide)
                return;

            await ShowDepotBoxGuideAsync();
            await _settingsService.UpdateAsync(s => s.HasSeenDepotBoxGuide = true);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "DepotBox first-time guide failed");
        }
    }

    private async Task ShowDepotBoxGuideAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "How to use DepotBox",
            Content = new TextBlock
            {
                Text =
                    "1. Search for your game using the search function. You can search by name or paste the game's App ID\n" +
                    "2. Press the \"Download .zip\" button\n" +
                    "3. Wait for the download\n" +
                    "4. Proceed with the prompts you get after that",
                TextWrapping = TextWrapping.WrapWholeWords
            },
            PrimaryButtonText = "OK",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
        };
        dialog.Resources["ContentDialogMinHeight"] = 0.0;
        dialog.Resources["ContentDialogMinWidth"] = 320.0;

        await dialog.ShowAsync();
    }

    private async Task EnsureDepotBoxReadyAsync()
    {
        if (_depotBoxReady)
            return;

        try
        {
            string userData = Path.Combine(AppPaths.DataDirectory, "WebView2");
            Directory.CreateDirectory(userData);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userData);
            await DepotBoxBrowser.EnsureCoreWebView2Async();
            DepotBoxBrowser.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            DepotBoxBrowser.CoreWebView2.Settings.IsReputationCheckingRequired = false;

            DepotBoxBrowser.NavigationStarting += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Uri))
                    DepotBoxAddressBox.Text = args.Uri;
            };
            DepotBoxBrowser.NavigationCompleted += (_, _) => UpdateDepotBoxChrome();
            DepotBoxBrowser.CoreWebView2.HistoryChanged += (_, _) => UpdateDepotBoxChrome();
            DepotBoxBrowser.CoreWebView2.SourceChanged += (_, _) =>
            {
                string source = DepotBoxBrowser.CoreWebView2.Source;
                if (!string.IsNullOrWhiteSpace(source))
                    DepotBoxAddressBox.Text = source;
            };
            DepotBoxBrowser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                string uri = args.Uri ?? string.Empty;
                if (Uri.TryCreate(uri, UriKind.Absolute, out Uri? target) &&
                    (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp))
                {
                    DepotBoxBrowser.CoreWebView2.Navigate(uri);
                }
            };
            DepotBoxBrowser.CoreWebView2.DownloadStarting += DepotBox_DownloadStarting;

            DepotBoxAddressBox.Text = DepotBoxHomeUrl;
            _depotBoxReady = true;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "DepotBox browser failed to start");
            _depotBoxOpen = false;
            UpdateEmptyState();
            await _messageBoxService.ShowAsync(
                "Browser unavailable",
                "Install the Microsoft Edge WebView2 Runtime to use DepotBox.\n\n" + ex.Message);
        }
    }

    private void DepotBoxBack_Click(object sender, RoutedEventArgs e)
    {
        if (DepotBoxBrowser.CanGoBack)
            DepotBoxBrowser.GoBack();
    }

    private void DepotBoxForward_Click(object sender, RoutedEventArgs e)
    {
        if (DepotBoxBrowser.CanGoForward)
            DepotBoxBrowser.GoForward();
    }

    private void DepotBoxClose_Click(object sender, RoutedEventArgs e)
    {
        ReleaseBrowser();
    }

    public void ReleaseBrowser()
    {
        if (!_depotBoxReady && DepotBoxBrowser.CoreWebView2 is null)
        {
            _depotBoxOpen = false;
            UpdateEmptyState();
            return;
        }

        bool hadBrowser = true;
        try
        {
            DepotBoxBrowser.CoreWebView2?.Stop();
            DepotBoxBrowser.Close();
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Downloads] Browser close: {ex.Message}");
        }

        DepotBoxBrowserHost.Children.Clear();
        DepotBoxBrowser = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        DepotBoxBrowserHost.Children.Add(DepotBoxBrowser);
        _depotBoxReady = false;
        _depotBoxOpen = false;
        DepotBoxAddressBox.Text = string.Empty;
        UpdateEmptyState();
        if (hadBrowser)
            AppLog.Write("[Downloads] WebView released");
    }

    private void DepotBoxGo_Click(object sender, RoutedEventArgs e) => NavigateDepotBoxAddress();

    private void DepotBoxAddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            NavigateDepotBoxAddress();
        }
    }

    private void NavigateDepotBoxAddress()
    {
        if (!_depotBoxReady)
            return;

        string text = DepotBoxAddressBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }

        DepotBoxBrowser.Source = uri;
    }

    private void UpdateDepotBoxChrome()
    {
        DepotBoxBackButton.IsEnabled = DepotBoxBrowser.CanGoBack;
        DepotBoxForwardButton.IsEnabled = DepotBoxBrowser.CanGoForward;
        if (DepotBoxBrowser.Source is not null)
            DepotBoxAddressBox.Text = DepotBoxBrowser.Source.ToString();
    }

    private void DepotBox_DownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
    {
        if (!IsZipDownload(args))
            return;

        string folder = Path.Combine(AppPaths.DataDirectory, "DepotBoxDownloads");
        Directory.CreateDirectory(folder);

        string suggested = Path.GetFileName(args.ResultFilePath);
        if (string.IsNullOrWhiteSpace(suggested))
            suggested = "manifest.zip";
        suggested = SanitizeDepotBoxFileName(suggested);
        if (suggested.EndsWith("!zip", StringComparison.OrdinalIgnoreCase))
            suggested = suggested[..^4] + ".zip";
        if (!suggested.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            suggested += ".zip";

        string dest = Path.Combine(folder, suggested);
        if (File.Exists(dest))
        {
            dest = Path.Combine(
                folder,
                $"{Path.GetFileNameWithoutExtension(suggested)}_{DateTime.UtcNow:yyyyMMddHHmmssfff}.zip");
        }

        args.ResultFilePath = dest;
        args.Handled = true;
        CoreWebView2DownloadOperation download = args.DownloadOperation;
        DispatcherQueue.TryEnqueue(() => _ = TrackDepotBoxDownloadAsync(download, dest));
        AppLog.Write($"[Downloads] DepotBox zip → {dest}");
    }

    private async Task TrackDepotBoxDownloadAsync(CoreWebView2DownloadOperation download, string path)
    {
        string fileName = Path.GetFileName(path);
        var statusText = new TextBlock
        {
            Text = $"Downloading {fileName}…",
            TextWrapping = TextWrapping.WrapWholeWords
        };
        var detailText = new TextBlock
        {
            Text = "Starting…",
            FontSize = 12
        };
        if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out object? secondary) &&
            secondary is Brush brush)
        {
            detailText.Foreground = brush;
        }

        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 8,
            IsIndeterminate = true
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(statusText);
        content.Children.Add(progressBar);
        content.Children.Add(detailText);

        var dialog = new ContentDialog
        {
            Title = "Downloading manifest",
            Content = content,
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
        };
        dialog.Resources["ContentDialogMinHeight"] = 0.0;
        dialog.Resources["ContentDialogMinWidth"] = 420.0;
        dialog.Resources["ContentDialogMaxWidth"] = 560.0;

        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool allowClose = false;

        void UpdateProgress()
        {
            long received = (long)download.BytesReceived;
            long total = (long)download.TotalBytesToReceive;
            if (total > 0)
            {
                progressBar.IsIndeterminate = false;
                progressBar.Value = Math.Clamp(received * 100.0 / total, 0, 100);
                detailText.Text = $"{AppLog.FormatBytes(received)} / {AppLog.FormatBytes(total)}";
            }
            else
            {
                progressBar.IsIndeterminate = true;
                detailText.Text = AppLog.FormatBytes(received);
            }
        }

        void OnBytesReceived(CoreWebView2DownloadOperation sender, object args) =>
            DispatcherQueue.TryEnqueue(UpdateProgress);

        void OnStateChanged(CoreWebView2DownloadOperation sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (sender.State == CoreWebView2DownloadState.InProgress)
                {
                    UpdateProgress();
                    return;
                }

                bool ok = sender.State == CoreWebView2DownloadState.Completed ||
                          IsUsableDepotBoxZip(path, sender.InterruptReason);
                if (!ok)
                {
                    AppLog.Write(
                        $"[Downloads] DepotBox download {sender.State} interrupt={sender.InterruptReason} file={path}");
                }

                finished.TrySetResult(ok);
            });
        }

        download.BytesReceivedChanged += OnBytesReceived;
        download.StateChanged += OnStateChanged;
        if (download.State != CoreWebView2DownloadState.InProgress)
            OnStateChanged(download, EventArgs.Empty);
        dialog.Closing += (_, args) =>
        {
            if (allowClose)
                return;

            args.Cancel = true;
            try
            {
                download.Cancel();
            }
            catch (Exception ex)
            {
                AppLog.Write(ex, "DepotBox download cancel failed");
            }
        };

        UpdateProgress();
        var showTask = dialog.ShowAsync().AsTask();
        bool succeeded;
        try
        {
            succeeded = await finished.Task;
        }
        finally
        {
            download.BytesReceivedChanged -= OnBytesReceived;
            download.StateChanged -= OnStateChanged;
            allowClose = true;
            try
            {
                dialog.Hide();
                await showTask;
            }
            catch
            {
            }
        }

        if (!succeeded)
        {
            await _messageBoxService.ShowAsync(
                "Download cancelled",
                $"The manifest download did not finish.\n{fileName}");
            PatchApplyService.TryDeleteFile(path);
            return;
        }

        PatchApplyService.UnblockFile(path);
        await ImportDepotBoxZipAsync(path);
    }

    private static bool IsUsableDepotBoxZip(string path, CoreWebView2DownloadInterruptReason reason)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            return false;
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return false;

        return reason is CoreWebView2DownloadInterruptReason.None
            or CoreWebView2DownloadInterruptReason.FileMalicious
            or CoreWebView2DownloadInterruptReason.FileSecurityCheckFailed
            or CoreWebView2DownloadInterruptReason.FileBlockedByPolicy;
    }

    private async Task ImportDepotBoxZipAsync(string path)
    {
        _depotBoxOpen = false;
        UpdateEmptyState();

        try
        {
            await ImportAndDownloadAsync(new[] { path });
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                AppLog.Write(ex, $"Could not delete DepotBox zip {path}");
            }
        }
    }

    private static bool IsZipDownload(CoreWebView2DownloadStartingEventArgs args)
    {
        string name = Path.GetFileName(args.ResultFilePath ?? string.Empty);
        string uri = args.DownloadOperation.Uri ?? string.Empty;
        if (name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ||
            uri.Contains(".lua", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("!zip", StringComparison.OrdinalIgnoreCase) ||
            uri.Contains(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) &&
               parsed.Host.Contains("depotbox", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeDepotBoxFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "manifest.zip" : name;
    }

    private enum ManifestPickKind
    {
        Zip,
        Folder
    }

    private async Task<ManifestPickKind?> PromptZipOrFolderAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.WrapWholeWords
            },
            PrimaryButtonText = ".zip files",
            SecondaryButtonText = "Folders",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = ResolveDialogXamlRoot(),
            RequestedTheme = ResolveDialogTheme()
        };
        dialog.Resources["ContentDialogMinWidth"] = 360.0;
        dialog.Resources["ContentDialogMaxWidth"] = 520.0;

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => ManifestPickKind.Zip,
            ContentDialogResult.Secondary => ManifestPickKind.Folder,
            _ => null
        };
    }

    private async Task ImportAndDownloadAsync(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
            await ImportManifestAsync(path);
    }

    private async Task<IReadOnlyList<string>?> PickZipFilesAsync()
    {
        IReadOnlyList<string> files = await _filePicker.PickFilesAsync(
            new[] { ".zip" },
            "Select manifest zip files",
            KnownExplorerFolders.Desktop);
        return files.Count == 0 ? null : files;
    }

    private Task<IReadOnlyList<string>> PickManifestFoldersAsync() =>
        _filePicker.PickFoldersAsync("Select manifest folders", KnownExplorerFolders.Desktop);

    /// <summary>Called from Library when the user taps Install on a library-only title.</summary>
    public async Task BeginInstallFromLibraryAsync(GameEntry game)
    {
        if (game is null || string.IsNullOrWhiteSpace(game.AppId))
            return;

        if (IsAppCurrentlyDownloading(game.AppId))
        {
            await _messageBoxService.ShowAsync(
                "Already downloading",
                $"\"{game.Name}\" is currently in the download process.");
            return;
        }

        string? extractionDir = ManifestArchiveService.FindExtractionDirectory(game.AppId);
        string? luaPath = ManifestArchiveService.FindLuaPath(game.AppId);
        if (extractionDir is null || luaPath is null)
        {
            await _messageBoxService.ShowAsync(
                "Manifests missing",
                $"No extracted manifest archive was found for {game.Name} (App ID {game.AppId}).\n\n" +
                "Use Browse Manifest Archive for library adding again, or Browse Manifest Archive for download.");
            return;
        }

        _finalPath = extractionDir;
        _appId = game.AppId;
        _currentGameName = string.IsNullOrWhiteSpace(game.Name) ? $"Steam App {_appId}" : game.Name;
        _currentCoverArtPath = game.Image;
        _currentLogoPath = string.IsNullOrWhiteSpace(game.Image)
            ? string.Empty
            : Path.Combine(Path.GetDirectoryName(game.Image) ?? string.Empty, "GameLogo.png");
        _preserveLibraryOnCancel = true;
        _wasInstalledBeforeDownload = game.IsInstalled;
        _installPathBeforeDownload = game.InstallPath ?? string.Empty;

        AppLog.Write($"[Downloads] Install from library appId={_appId} dir={_finalPath}");
        await ManifestDepotIdChoiceAsync(luaPath);
    }

    private void ManifestDropZone_DragEnter(object sender, DragEventArgs e)
    {
        _dragDepth++;
        UpdateDropTarget(e, highlight: true);
    }

    private void ManifestDropZone_DragOver(object sender, DragEventArgs e) =>
        UpdateDropTarget(e, highlight: true);

    private void ManifestDropZone_DragLeave(object sender, DragEventArgs e)
    {
        _dragDepth = Math.Max(0, _dragDepth - 1);
        if (_dragDepth == 0)
            SetDropHighlight(false);
    }

    private async void ManifestDropZone_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragDepth = 0;
        SetDropHighlight(false);

        IReadOnlyList<string> paths = await TryGetDroppedManifestPathsAsync(e);
        if (paths.Count == 0)
        {
            _notifications.Show(
                "Invalid drop",
                "Drop .zip file(s) or folder(s) that contain a .lua file.",
                InfoBarSeverity.Warning);
            return;
        }

        await ImportAndDownloadAsync(paths);
    }

    private void UpdateDropTarget(DragEventArgs e, bool highlight)
    {
        bool canAccept = e.DataView.Contains(StandardDataFormats.StorageItems);
        if (!canAccept)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            SetDropHighlight(false);
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = "Drop .zip or folder(s)";
        if (highlight)
            SetDropHighlight(true);
    }

    private void SetDropHighlight(bool on)
    {
        if (_dropHighlight == on)
            return;

        _dropHighlight = on;
        DropHighlightOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (EmptyDropPrompt.Visibility == Visibility.Visible)
        {
            object? accent = null;
            Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out accent);
            object? cardStroke = null;
            Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out cardStroke);

            EmptyDropPrompt.BorderBrush = on
                ? accent as Brush ?? new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue)
                : cardStroke as Brush ?? EmptyDropPrompt.BorderBrush;
            EmptyDropPrompt.BorderThickness = new Thickness(on ? 3 : 2);
        }

        DropZoneHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DropZoneHint.Text = on
            ? "Release to import .zip or folder(s)"
            : "Drop .zip archives or folders here\nEach must contain a .lua file";
    }

    private static async Task<IReadOnlyList<string>> TryGetDroppedManifestPathsAsync(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return Array.Empty<string>();

        IReadOnlyList<IStorageItem> items;
        try
        {
            items = await e.DataView.GetStorageItemsAsync();
        }
        catch
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>(items.Count);
        foreach (IStorageItem item in items)
        {
            if (item is StorageFile file &&
                file.FileType.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(file.Path);
            }
            else if (item is StorageFolder folder)
            {
                paths.Add(folder.Path);
            }
        }

        return paths;
    }

    private async Task ImportManifestAsync(string path)
    {
        // Drop can bubble through Page/Grid/ScrollViewer/EmptyPrompt — only import once.
        if (Interlocked.CompareExchange(ref _importBusy, 1, 0) != 0)
        {
            AppLog.Write($"[Downloads] Import ignored (already in progress): {path}");
            return;
        }

        try
        {
            await ImportManifestCoreAsync(path);
        }
        catch (Exception ex)
        {
            _notifications.Show("Import failed", ex.Message, InfoBarSeverity.Error);
            AppLog.Write(ex, "Manifest import failed");
        }
        finally
        {
            Interlocked.Exchange(ref _importBusy, 0);
        }
    }

    private async Task ImportManifestCoreAsync(string path)
    {
        AppLog.Write($"[Downloads] Importing: {path}");
        var archive = await _archiveService.ExtractAsync(path);
        _finalPath = archive.ExtractionDirectory;
        _appId = archive.AppId;
        _currentGameName = $"Steam App {_appId}";
        _currentLogoPath = archive.LogoPath;
        _currentCoverArtPath = archive.CoverArtPath;
        AppLog.Write(
            $"[Downloads] Imported appId={_appId} dir={_finalPath} lua={archive.LuaFilePath} " +
            $"logo={archive.LogoPath} cover={archive.CoverArtPath}");

        if (IsAppCurrentlyDownloading(_appId))
        {
            string existingName = Downloads
                .FirstOrDefault(d => string.Equals(d.AppId, _appId, StringComparison.OrdinalIgnoreCase))
                ?.GameName
                ?? _currentGameName;

            AppLog.Write($"[Downloads] Rejected duplicate import appId={_appId} ('{existingName}') — already downloading");
            await _messageBoxService.ShowAsync(
                "Already downloading",
                $"\"{existingName}\" is currently in the download process and cannot be added again.");
            return;
        }

        await _steamMetadata.DownloadArtworkAsync(_appId, archive.LogoPath, archive.CoverArtPath, archive.HeroPath, archive.IconPath);

        var existing = (await _gameLibrary.LoadAsync())
            .FirstOrDefault(g => string.Equals(g.AppId, _appId, StringComparison.OrdinalIgnoreCase));
        _wasInstalledBeforeDownload = existing?.IsInstalled ?? false;
        _installPathBeforeDownload = existing?.InstallPath ?? string.Empty;
        _preserveLibraryOnCancel = true;

        if (existing is null)
        {
            await AddSteamGameToLibraryAsync(_appId, archive.CoverArtPath, isInstalled: false);
        }
        else
        {
            try
            {
                string? resolvedName = await _steamMetadata.GetGameNameAsync(_appId);
                if (!string.IsNullOrWhiteSpace(resolvedName))
                    _currentGameName = resolvedName;
            }
            catch (Exception ex)
            {
                AppLog.Write(ex, "Resolve game name for existing library title failed");
            }

            AppLog.Write(
                $"[Downloads] App {_appId} already in library — keeping entry on cancel " +
                $"(wasInstalled={_wasInstalledBeforeDownload})");
        }

        await ManifestDepotIdChoiceAsync(archive.LuaFilePath);
    }

    public bool HasActiveDownloads => Downloads.Count > 0;

    public int ActiveDownloadCount => Downloads.Count;

    public bool IsAppInstalling(string appId) =>
        !string.IsNullOrWhiteSpace(appId) &&
        (_pendingInstallAppIds.Contains(appId) || IsAppCurrentlyDownloading(appId));

    public async Task CancelAllDownloadsAndWaitAsync()
    {
        foreach (var item in Downloads.ToList())
            item.CancelCommand?.Execute(null);

        var started = Stopwatch.StartNew();
        while (Downloads.Count > 0 && started.Elapsed < TimeSpan.FromSeconds(20))
            await Task.Delay(100);
    }

    private bool IsAppCurrentlyDownloading(string appId) =>
        !string.IsNullOrWhiteSpace(appId) &&
        Downloads.Any(d => string.Equals(d.AppId, appId, StringComparison.OrdinalIgnoreCase));

    private async Task AddSteamGameToLibraryAsync(string appId, string coverArt, bool isInstalled)
    {
        string gameName;
        try
        {
            gameName = await _steamMetadata.GetGameNameAsync(appId);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Resolve game name failed");
            gameName = string.Empty;
        }

        _currentGameName = string.IsNullOrWhiteSpace(gameName) ? $"Steam App {appId}" : gameName;
        AppLog.Write($"[Downloads] Resolved game name appId={appId} → '{_currentGameName}'");
        string installPath = isInstalled
            ? await _installPathService.GetInstallDirectoryAsync(
                _currentGameName,
                appId,
                promptIfMissing: false)
            : string.Empty;
        await _gameLibrary.UpsertAsync(new GameEntry
        {
            AppId = appId,
            Name = _currentGameName,
            Image = coverArt,
            StartLocation = string.Empty,
            InstallPath = installPath,
            IsInstalled = isInstalled
        });

        RefreshService.RequestRefresh();
        _notifications.Show("Success", "Game added successfully!", InfoBarSeverity.Success);
        AppLog.Write($"[Downloads] Library upserted installPath={installPath} isInstalled={isInstalled}");
    }

    private async Task ManifestDepotIdChoiceAsync(string luaFilePath)
    {
        string appId = _appId;
        MarkPendingInstall(appId);
        try
        {
            await ManifestDepotIdChoiceCoreAsync(luaFilePath);
        }
        finally
        {
            UnmarkPendingInstall(appId);
        }
    }

    private void MarkPendingInstall(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !_pendingInstallAppIds.Add(appId))
            return;

        InstallingChanged?.Invoke();
    }

    private void UnmarkPendingInstall(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !_pendingInstallAppIds.Remove(appId))
            return;

        InstallingChanged?.Invoke();
    }

    private async Task ManifestDepotIdChoiceCoreAsync(string luaFilePath)
    {
        var availableItems = _manifestParser.Parse(luaFilePath);
        var depotIds = availableItems.Select(item => item.DepotId).ToList();
        var relatedAppIds = _manifestParser.ParseRelatedAppIds(luaFilePath).ToList();

        var loading = new ContentDialog
        {
            Title = "Selecting right depots...",
            Content = new TextBlock
            {
                Text = "Loading Windows depots from Steam...",
                TextWrapping = TextWrapping.WrapWholeWords
            },
            XamlRoot = ResolveDialogXamlRoot(),
            RequestedTheme = ResolveDialogTheme()
        };
        var loadingShow = loading.ShowAsync();
        IReadOnlyDictionary<string, DepotMetadata> metadata;
        try
        {
            string appId = _appId;
            metadata = await Task.Run(async () =>
                await _depotMetadata.GetDepotMetadataAsync(appId, depotIds, relatedAppIds)
                    .ConfigureAwait(false));
        }
        finally
        {
            loading.Hide();
            try
            {
                await loadingShow;
            }
            catch (Exception)
            {
            }
        }

        var displayRows = BuildDepotDisplayRows(availableItems, metadata);
        var gameRows = SteamDepotPlatformFilter.PreferHostArch(
            displayRows
                .Where(row =>
                    !row.Display.IsLanguage
                    && !row.Display.IsDlc
                    && !row.Display.IsShared)
                .ToList());
        var dlcRows = SteamDepotPlatformFilter.PreferHostArch(
            displayRows
                .Where(row => row.Display.IsDlc)
                .OrderBy(row => DlcGroupName(row.Display), StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => DlcLanguageRank(row.Display))
                .ThenBy(row => row.Display.TypeLabel, StringComparer.OrdinalIgnoreCase)
                .ToList());
        var languageRows = SteamDepotPlatformFilter.PreferHostArch(
            displayRows
                .Where(row => row.Display.IsLanguage && !row.Display.IsDlc)
                .OrderBy(row => IsEnglishLanguage(row.Display.LanguageCode) ? 0 : 1)
                .ThenBy(row => row.Display.TypeLabel, StringComparer.OrdinalIgnoreCase)
                .ToList());

        // Some titles (MGSV) ship the game itself as language depots. Those belong
        // in this step — not an empty game list plus an optional language dialog.
        if (gameRows.Count == 0 && languageRows.Count > 0)
        {
            gameRows = RelabelLanguageAsGame(languageRows);
            languageRows = [];
        }

        var executePostDownloadCheck = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "Remove Steam DRM",
                Margin = new Thickness(8, 0, 0, 0)
            },
            IsChecked = true,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        int windowsSelected = gameRows.Count(row => row.Display.AutoSelected);
        string gameHint = windowsSelected > 0
            ? $"Windows game files Steam would install ({windowsSelected}). You can change this."
            : metadata.Count == 0
                ? "Steam depot info unavailable. Select depots manually."
                : "No Windows depot match — select the game files you want.";

        var gamePick = await ShowDepotRowsDialogAsync(
            "Select game files",
            gameHint,
            gameRows,
            NextOrDownload(dlcRows.Count > 0 || languageRows.Count > 0),
            "Cancel",
            CreateDepotDialogHeader(executePostDownloadCheck));

        if (gamePick.Result != ContentDialogResult.Primary)
            return;

        if (gamePick.Selected.Count == 0 && dlcRows.Count == 0)
            return;

        var selectedItems = gamePick.Selected;
        if (dlcRows.Count > 0)
        {
            var dlcPick = await ShowDepotRowsDialogAsync(
                "Select DLC",
                "Optional. Language-specific DLC packs are listed under each DLC name.",
                dlcRows,
                NextOrDownload(languageRows.Count > 0),
                "Skip",
                CreateDepotDialogHeader(),
                typeHeader: "Name");
            if (dlcPick.Result == ContentDialogResult.Primary)
                selectedItems.AddRange(dlcPick.Selected);
        }

        if (languageRows.Count > 0)
        {
            var languagePick = await ShowDepotRowsDialogAsync(
                "Select language",
                "Optional. Leave none selected to skip extra languages.",
                languageRows,
                "Install Selected",
                "Skip",
                CreateDepotDialogHeader());
            if (languagePick.Result == ContentDialogResult.Primary)
                selectedItems.AddRange(languagePick.Selected);
        }

        if (selectedItems.Count == 0)
            return;

        await StartDownloadProcessAsync(
            selectedItems,
            executePostDownload: executePostDownloadCheck.IsChecked == true);
        RefreshService.RequestRefresh();
    }

    private static string NextOrDownload(bool hasMore) =>
        hasMore ? "Next" : "Install Selected";

    private static List<(DepotInfo Depot, DepotDisplayInfo Display)> RelabelLanguageAsGame(
        IReadOnlyList<(DepotInfo Depot, DepotDisplayInfo Display)> languageRows)
    {
        var result = languageRows
            .Select(row =>
            {
                string? language = FormatSteamLanguage(row.Display.LanguageCode);
                return (Depot: row.Depot, Display: row.Display with
                {
                    TypeLabel = string.IsNullOrWhiteSpace(language) ? "Game" : $"Game — {language}"
                });
            })
            .ToList();

        if (result.Count > 0 && !result.Any(row => row.Display.AutoSelected))
        {
            var first = result[0];
            result[0] = (Depot: first.Depot, Display: first.Display with { AutoSelected = first.Display.HasLocalManifest });
        }

        return result;
    }

    private FrameworkElement CreateDepotDialogHeader(UIElement? extra = null)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(0, 0, 0, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(new TextBlock
        {
            Text = $"App ID {_appId}",
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new HyperlinkButton
        {
            Content = "SteamDB depots",
            NavigateUri = new Uri($"https://steamdb.info/app/{_appId}/depots/"),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        });
        if (extra is not null)
            row.Children.Add(extra);
        return row;
    }

    private async Task<(ContentDialogResult Result, List<DepotInfo> Selected)> ShowDepotRowsDialogAsync(
        string title,
        string hint,
        IReadOnlyList<(DepotInfo Depot, DepotDisplayInfo Display)> rows,
        string primaryButton,
        string closeButton,
        FrameworkElement? headerExtra,
        string typeHeader = "Type")
    {
        var root = new Grid
        {
            RowSpacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBlock = new TextBlock
        {
            Text = _currentGameName,
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            TextWrapping = TextWrapping.NoWrap
        };
        Grid.SetRow(titleBlock, 0);
        root.Children.Add(titleBlock);

        if (headerExtra is not null)
        {
            Grid.SetRow(headerExtra, 1);
            root.Children.Add(headerExtra);
        }

        var hintBlock = new TextBlock
        {
            Text = hint,
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.WrapWholeWords
        };
        Grid.SetRow(hintBlock, 2);
        root.Children.Add(hintBlock);

        var header = CreateDepotTableHeader(typeHeader, out var checkBoxes);
        var body = CreateDepotTableBody(rows, checkBoxes);
        Grid.SetRow(header, 3);
        root.Children.Add(header);

        var scrollViewer = new ScrollViewer
        {
            Content = body,
            MaxHeight = 420,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 0, 8, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled
        };
        Grid.SetRow(scrollViewer, 4);
        root.Children.Add(scrollViewer);

        var dialog = new ContentDialog
        {
            Title = title,
            PrimaryButtonText = primaryButton,
            CloseButtonText = closeButton,
            Content = root,
            XamlRoot = ResolveDialogXamlRoot(),
            RequestedTheme = ResolveDialogTheme()
        };
        dialog.Resources["ContentDialogMinWidth"] = 480.0;
        dialog.Resources["ContentDialogMaxWidth"] = 980.0;

        var result = await dialog.ShowAsync();
        var selected = checkBoxes
            .Where(box => box.IsChecked == true)
            .Select(box => (DepotInfo)box.Tag)
            .ToList();
        return (result, selected);
    }

    private static void AddDepotColumns(Grid grid)
    {
        grid.ColumnSpacing = 12;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 152 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star), MinWidth = 100 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
    }

    private static Grid CreateDepotTableHeader(string typeHeader, out List<CheckBox> checkBoxes)
    {
        var header = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 24, 0)
        };
        AddDepotColumns(header);
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void AddHeaderCell(string text, int column)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.7,
                TextAlignment = TextAlignment.Start,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(block, 0);
            Grid.SetColumn(block, column);
            header.Children.Add(block);
        }

        AddHeaderCell("Depot ID", 1);
        AddHeaderCell("Manifest ID", 2);
        AddHeaderCell(typeHeader, 3);
        AddHeaderCell("DL size", 4);

        checkBoxes = new List<CheckBox>();
        var uncheckAllButton = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uE711",
                FontSize = 10
            },
            Width = 20,
            Height = 20,
            MinWidth = 20,
            MinHeight = 20,
            MaxWidth = 20,
            MaxHeight = 20,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(uncheckAllButton, "Uncheck all");
        var boxes = checkBoxes;
        uncheckAllButton.Click += (_, _) =>
        {
            foreach (CheckBox box in boxes)
            {
                if (box.IsEnabled)
                    box.IsChecked = false;
            }
        };

        var uncheckHost = new Grid
        {
            Width = 32,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        uncheckHost.Children.Add(uncheckAllButton);
        Grid.SetRow(uncheckHost, 0);
        Grid.SetColumn(uncheckHost, 0);
        header.Children.Add(uncheckHost);
        return header;
    }

    private static Grid CreateDepotTableBody(
        IReadOnlyList<(DepotInfo Depot, DepotDisplayInfo Display)> rows,
        List<CheckBox> checkBoxes)
    {
        var table = new Grid
        {
            RowSpacing = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 16, 0)
        };
        AddDepotColumns(table);

        for (int i = 0; i < rows.Count; i++)
        {
            int rowIndex = i;
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = rows[i];

            var checkBox = new CheckBox
            {
                Tag = row.Depot,
                IsChecked = row.Display.AutoSelected,
                IsEnabled = row.Display.HasLocalManifest,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 32,
                Height = 32,
                MinWidth = 32,
                Padding = new Thickness(0),
                Margin = new Thickness(0)
            };
            checkBoxes.Add(checkBox);

            var hitTarget = new Border
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            Grid.SetRow(hitTarget, rowIndex);
            Grid.SetColumn(hitTarget, 0);
            Grid.SetColumnSpan(hitTarget, 5);
            hitTarget.PointerPressed += (_, args) =>
            {
                if (!checkBox.IsEnabled)
                    return;
                checkBox.IsChecked = checkBox.IsChecked != true;
                args.Handled = true;
            };
            table.Children.Add(hitTarget);

            Grid.SetRow(checkBox, rowIndex);
            Grid.SetColumn(checkBox, 0);
            table.Children.Add(checkBox);

            var appIdText = new TextBlock
            {
                Text = row.Display.DepotId,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextAlignment = TextAlignment.Start,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            Grid.SetRow(appIdText, rowIndex);
            Grid.SetColumn(appIdText, 1);
            table.Children.Add(appIdText);

            var manifestText = new TextBlock
            {
                Text = row.Display.ManifestId,
                TextAlignment = TextAlignment.Start,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            Grid.SetRow(manifestText, rowIndex);
            Grid.SetColumn(manifestText, 2);
            table.Children.Add(manifestText);

            var typeText = new TextBlock
            {
                Text = row.Display.TypeLabel,
                TextAlignment = TextAlignment.Start,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                IsHitTestVisible = false
            };
            if (!string.IsNullOrWhiteSpace(row.Display.DepotName))
                ToolTipService.SetToolTip(typeText, row.Display.DepotName);
            Grid.SetRow(typeText, rowIndex);
            Grid.SetColumn(typeText, 3);
            table.Children.Add(typeText);

            var sizeText = new TextBlock
            {
                Text = row.Display.DownloadText,
                TextAlignment = TextAlignment.Start,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            Grid.SetRow(sizeText, rowIndex);
            Grid.SetColumn(sizeText, 4);
            table.Children.Add(sizeText);
        }

        return table;
    }

    private List<(DepotInfo Depot, DepotDisplayInfo Display)> BuildDepotDisplayRows(
        IReadOnlyList<DepotInfo> depots,
        IReadOnlyDictionary<string, DepotMetadata> metadata)
    {
        var staged = new List<(DepotInfo Depot, DepotDisplayInfo Display, DepotMetadata? Meta)>();
        foreach (var depot in depots)
        {
            metadata.TryGetValue(depot.DepotId, out var meta);
            string? manifestPath = ResolveManifestPath(depot);
            bool hasManifest = manifestPath is not null;

            long? size = meta?.SizeBytes;
            long? download = meta?.DownloadBytes;
            if (hasManifest)
            {
                var local = TryReadLocalManifestSizes(depot);
                size ??= local.Size;
                download ??= local.Download;
            }

            // Prefer download size; fall back to full size when Steam only exposes one value.
            download ??= size;

            string languageCode = FirstLanguageCode(meta?.Language, meta?.Name, meta?.Configuration) ?? string.Empty;
            staged.Add((depot, new DepotDisplayInfo
            {
                DepotId = depot.DepotId,
                ManifestId = depot.ManifestId,
                Configuration = meta?.Configuration ?? string.Empty,
                TypeLabel = FormatDepotTypeLabel(meta, depot.DepotId, _currentGameName),
                DepotName = meta?.Name ?? string.Empty,
                SizeBytes = size,
                DownloadBytes = download,
                HasLocalManifest = hasManifest,
                IsDlc = meta?.IsDlc == true,
                IsShared = meta?.IsShared == true,
                IsLanguage = !string.IsNullOrWhiteSpace(languageCode),
                LanguageCode = languageCode,
                OsArch = meta?.OsArch
            }, meta));
        }

        staged = staged
            .Where(row => !SteamDepotPlatformFilter.IsMacOsOrLinuxOnly(row.Meta, row.Display))
            .ToList();

        var windowsIds = SteamDepotPlatformFilter.SelectWindowsDepotIds(staged);
        AppLog.Write(
            $"[Downloads] Windows auto-select {windowsIds.Count}/{staged.Count} depot(s): " +
            string.Join(", ", windowsIds));
        foreach (var row in staged)
        {
            AppLog.Write(
                $"[Downloads] depot {row.Depot.DepotId} os={row.Meta?.OsList ?? "(none)"} " +
                $"lang={row.Display.LanguageCode} dlc={row.Display.IsDlc} " +
                $"type={row.Display.TypeLabel} selected={windowsIds.Contains(row.Depot.DepotId)}");
        }

        return staged
            .OrderBy(row => SteamDepotPlatformFilter.ListRank(row.Meta, row.Display))
            .ThenBy(row => string.IsNullOrWhiteSpace(row.Meta?.Language) ? 0 : 1)
            .ThenBy(row => row.Meta?.IsDlc == true ? 1 : 0)
            .ThenBy(row => row.Depot.DepotId, StringComparer.Ordinal)
            .Select(row =>
            {
                bool isEnglish = IsEnglishLanguage(row.Display.LanguageCode);
                var display = new DepotDisplayInfo
                {
                    DepotId = row.Display.DepotId,
                    ManifestId = row.Display.ManifestId,
                    Configuration = row.Display.Configuration,
                    TypeLabel = row.Display.TypeLabel,
                    DepotName = row.Display.DepotName,
                    SizeBytes = row.Display.SizeBytes,
                    DownloadBytes = row.Display.DownloadBytes,
                    HasLocalManifest = row.Display.HasLocalManifest,
                    IsDlc = row.Display.IsDlc,
                    IsShared = row.Display.IsShared,
                    IsLanguage = row.Display.IsLanguage,
                    LanguageCode = row.Display.LanguageCode,
                    OsArch = row.Display.OsArch,
                    AutoSelected = row.Display.HasLocalManifest && (
                        row.Display.IsDlc
                        || (row.Display.IsLanguage && isEnglish)
                        || (!row.Display.IsLanguage && windowsIds.Contains(row.Depot.DepotId)))
                };
                return (row.Depot, display);
            })
            .ToList();
    }

    private static string FormatDepotTypeLabel(DepotMetadata? meta, string depotId, string gameName)
    {
        string? language = FormatSteamLanguage(FirstLanguageCode(meta?.Language, meta?.Name));

        if (meta?.IsDlc == true)
        {
            string dlcName = FormatDlcName(meta, depotId, gameName);
            return string.IsNullOrWhiteSpace(language) ? dlcName : $"{dlcName} — {language}";
        }

        if (!string.IsNullOrWhiteSpace(language))
            return $"Language: {language}";

        return meta?.TypeLabel ?? "Game";
    }

    private static readonly Regex LocaleSuffixRegex = new(
        @"\s+-\s+(?:Content|[a-z]{2}(?:_[A-Za-z]{2})?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string FormatDlcName(DepotMetadata meta, string depotId, string gameName)
    {
        string original = meta.Name?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(meta.DlcAppId)
            && original.Equals($"DLC {meta.DlcAppId}", StringComparison.OrdinalIgnoreCase))
        {
            original = string.Empty;
        }

        string name = original;
        if (!string.IsNullOrWhiteSpace(name))
        {
            int paren = name.IndexOf(" (", StringComparison.Ordinal);
            if (paren >= 0)
                name = name[..paren];

            name = LocaleSuffixRegex.Replace(name, string.Empty).Trim();
            foreach (string suffix in new[] { " Depot", " depot", " デポ" })
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    name = name[..^suffix.Length].Trim();
            }

            if (!string.IsNullOrWhiteSpace(gameName)
                && name.StartsWith(gameName, StringComparison.OrdinalIgnoreCase))
            {
                string stripped = name[gameName.Length..].TrimStart(' ', '-', ':');
                name = string.IsNullOrWhiteSpace(stripped) ? name : stripped;
            }
            else
            {
                int dash = name.IndexOf(" - ", StringComparison.Ordinal);
                if (dash >= 0)
                {
                    string afterDash = name[(dash + 3)..].Trim();
                    if (!string.IsNullOrWhiteSpace(afterDash))
                        name = afterDash;
                }
            }

            if (!string.IsNullOrWhiteSpace(name)
                && !name.Equals(gameName, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }

            if (!string.IsNullOrWhiteSpace(original)
                && !original.Equals(gameName, StringComparison.OrdinalIgnoreCase))
            {
                return original;
            }
        }

        return string.IsNullOrWhiteSpace(meta.DlcAppId)
            ? $"DLC {depotId}"
            : $"DLC {meta.DlcAppId}";
    }

    private static string? FirstLanguageCode(params string?[] values)
    {
        string? steamLanguage = values.Length > 0 ? values[0] : null;
        if (!string.IsNullOrWhiteSpace(steamLanguage))
            return steamLanguage.Trim();

        for (int i = 1; i < values.Length; i++)
        {
            string? inferred = SteamLanguageNames.InferFromName(values[i]);
            if (!string.IsNullOrWhiteSpace(inferred))
                return inferred;
        }

        return null;
    }

    private static bool IsEnglishLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        string normalized = code.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized is "english" or "en" or "en_us" or "en_gb";
    }

    private static string DlcGroupName(DepotDisplayInfo display)
    {
        string label = display.TypeLabel;
        int separator = label.LastIndexOf(" — ", StringComparison.Ordinal);
        return separator >= 0 ? label[..separator] : label;
    }

    private static int DlcLanguageRank(DepotDisplayInfo display)
    {
        if (!display.IsLanguage)
            return 0;
        return IsEnglishLanguage(display.LanguageCode) ? 1 : 2;
    }

    private static string? FormatSteamLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        return code.Trim().ToLowerInvariant() switch
        {
            "arabic" => "Arabic",
            "brazilian" => "Portuguese - Brazil",
            "bulgarian" => "Bulgarian",
            "czech" => "Czech",
            "danish" => "Danish",
            "dutch" => "Dutch",
            "english" => "English",
            "finnish" => "Finnish",
            "french" => "French",
            "german" => "German",
            "greek" => "Greek",
            "hungarian" => "Hungarian",
            "indonesian" => "Indonesian",
            "italian" => "Italian",
            "japanese" => "Japanese",
            "koreana" => "Korean",
            "latam" => "Spanish - Latin America",
            "norwegian" => "Norwegian",
            "polish" => "Polish",
            "portuguese" => "Portuguese - Portugal",
            "romanian" => "Romanian",
            "russian" => "Russian",
            "schinese" => "Simplified Chinese",
            "spanish" => "Spanish - Spain",
            "swedish" => "Swedish",
            "tchinese" => "Traditional Chinese",
            "thai" => "Thai",
            "turkish" => "Turkish",
            "ukrainian" => "Ukrainian",
            "vietnamese" => "Vietnamese",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(code.Replace('_', ' '))
        };
    }

    private string? ResolveManifestPath(DepotInfo depot)
    {
        if (!string.IsNullOrWhiteSpace(depot.ManifestPath) && File.Exists(depot.ManifestPath))
            return depot.ManifestPath;

        if (string.IsNullOrWhiteSpace(_finalPath))
            return null;

        string fallback = Path.Combine(_finalPath, $"{depot.DepotId}_{depot.ManifestId}.manifest");
        return File.Exists(fallback) ? fallback : null;
    }

    private (long? Size, long? Download) TryReadLocalManifestSizes(DepotInfo depot)
    {
        try
        {
            string? manifestPath = ResolveManifestPath(depot);
            if (manifestPath is null)
                return (null, null);

            byte[] data = File.ReadAllBytes(manifestPath);
            var manifest = SteamKit2.DepotManifest.Deserialize(data);
            if (manifest.Files is null)
                return (null, null);

            long size = 0;
            long download = 0;
            foreach (var file in manifest.Files)
            {
                foreach (var chunk in file.Chunks)
                {
                    size += chunk.UncompressedLength;
                    download += chunk.CompressedLength;
                }
            }

            return (size, download);
        }
        catch
        {
            return (null, null);
        }
    }

    private void EnsureElapsedTimer()
    {
        if (_elapsedTimer is null)
        {
            _elapsedTimer = DispatcherQueue.CreateTimer();
            _elapsedTimer.Interval = TimeSpan.FromSeconds(1);
            _elapsedTimer.Tick += (_, _) =>
            {
                foreach (var item in Downloads)
                    item.RefreshElapsed();

                if (Downloads.Count == 0)
                    _elapsedTimer.Stop();
            };
        }

        if (!_elapsedTimer.IsRunning)
            _elapsedTimer.Start();
    }

    private async Task<bool> HasEnoughDiskSpaceAsync(IReadOnlyList<DepotInfo> selectedDepots)
    {
        string installDirectory = await _installPathService
            .GetInstallDirectoryAsync(_currentGameName, _appId);
        long required = await EstimateSelectedInstallBytesAsync(selectedDepots);
        if (required <= 0)
            return true;

        if (!DriveSpace.TryGetAvailableBytes(installDirectory, out long available))
            return true;

        long needed = required + DriveSpace.SafetyReserveBytes;
        if (available >= needed)
            return true;

        string drive = DriveSpace.DriveName(installDirectory);
        AppLog.Write(
            $"[Downloads] Blocked install '{_currentGameName}' appId={_appId}: " +
            $"need={needed} free={available} drive={drive}");
        await _messageBoxService.ShowAsync(
            "Not enough space",
            $"\"{_currentGameName}\" needs {DriveSpace.FormatBytes(required)} to install on {drive}, " +
            $"but only {DriveSpace.FormatBytes(available)} is free.\n\n" +
            "Free up space or change the download folder in Settings, then try again.");
        return false;
    }

    private async Task<long> EstimateSelectedInstallBytesAsync(IReadOnlyList<DepotInfo> selectedDepots)
    {
        IReadOnlyDictionary<string, DepotMetadata> metadata = new Dictionary<string, DepotMetadata>();
        if (!string.IsNullOrWhiteSpace(_appId))
        {
            metadata = await _depotMetadata
                .GetDepotMetadataAsync(_appId, selectedDepots.Select(depot => depot.DepotId))
                .ConfigureAwait(false);
        }

        long total = 0;
        foreach (var depot in selectedDepots)
        {
            long? size = null;
            if (metadata.TryGetValue(depot.DepotId, out var meta))
                size = meta.SizeBytes;

            if (size is not > 0)
                size = TryReadLocalManifestSizes(depot).Size;

            if (size is > 0)
                total += size.Value;
        }

        return total;
    }

    private async Task StartDownloadProcessAsync(List<DepotInfo> selectedDepots, bool executePostDownload)
    {
        if (IsAppCurrentlyDownloading(_appId))
        {
            await _messageBoxService.ShowAsync(
                "Already downloading",
                $"\"{_currentGameName}\" is currently in the download process and cannot be added again.");
            return;
        }

        if (await _installPathService.TryEnsureDownloadRootAsync() is null)
        {
            await _messageBoxService.ShowAsync(
                "Install location required",
                "Choose a default install folder before downloading.");
            return;
        }

        if (!await HasEnoughDiskSpaceAsync(selectedDepots))
            return;

        var cancellation = new CancellationTokenSource();
        var pause = new DownloadPauseState();
        var downloadItem = new DownloadItem
        {
            GameName = _currentGameName,
            AppId = _appId,
            Status = $"Preparing {selectedDepots.Count} depot(s)...",
            IconSource = LoadImage(
                File.Exists(_currentLogoPath) ? _currentLogoPath : _currentCoverArtPath),
            ProgressValue = 0
        };

        bool cancelRequested = false;
        downloadItem.CancelCommand = new RelayCommand(() =>
        {
            if (cancelRequested)
                return;

            cancelRequested = true;
            downloadItem.Status = "Cancelling...";
            AppLog.Write($"[Downloads] Cancel requested for '{_currentGameName}' appId={_appId}");
            try
            {
                if (!cancellation.IsCancellationRequested)
                    cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                AppLog.Write("[Downloads] Cancel ignored: CancellationTokenSource already disposed");
            }
        });
        downloadItem.PauseCommand = new RelayCommand(() =>
        {
            if (cancelRequested)
                return;

            if (pause.Toggle())
            {
                downloadItem.PauseElapsed();
                downloadItem.Status = "Paused";
                downloadItem.PauseButtonText = "Resume";
                AppLog.Write($"[Downloads] Paused '{_currentGameName}'");
            }
            else
            {
                downloadItem.ResumeElapsed();
                downloadItem.Status = "Downloading game files...";
                downloadItem.PauseButtonText = "Pause";
                AppLog.Write($"[Downloads] Resumed '{_currentGameName}'");
            }
        });

        Downloads.Add(downloadItem);
        downloadItem.StartElapsed();
        EnsureElapsedTimer();
        bool preserveLibraryOnCancel = _preserveLibraryOnCancel;
        bool wasInstalledBeforeDownload = _wasInstalledBeforeDownload;
        string installPathBeforeDownload = _installPathBeforeDownload;
        AppLog.Write(
            $"[Downloads] Queued download '{_currentGameName}' appId={_appId} " +
            $"selectedDepots={selectedDepots.Count} ids=[{string.Join(", ", selectedDepots.Select(d => d.DepotId))}] " +
            $"preserveLibraryOnCancel={preserveLibraryOnCancel} wasInstalledBefore={wasInstalledBeforeDownload} " +
            $"executePostDownload={executePostDownload}");

        var progressReporter = new Progress<DownloadProgress>(progress =>
        {
            // Always hop to the WinUI dispatcher — Progress may invoke from a worker thread.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (cancelRequested)
                    return;

                downloadItem.DownloadedBytes = progress.DownloadedBytes;
                downloadItem.TotalBytes = progress.TotalBytes;
                downloadItem.NetworkBytesReceived = progress.NetworkBytesReceived;
                downloadItem.ProgressValue = progress.Percentage;
                if (progress.DownloadedBytes > 0 || progress.NetworkBytesReceived > 0)
                    downloadItem.Status = "Downloading game files...";
            });
        });

        _ = Task.Run(async () =>
        {
            bool downloadCompleted = false;
            string? downloadDest = null;
            string cancelledAppId = _appId;
            string cancelledGameName = _currentGameName;
            string cancelledCoverArt = _currentCoverArtPath;
            try
            {
                AppLog.Write($"[Downloads] Worker start '{cancelledGameName}' appId={cancelledAppId}");
                DispatcherQueue.TryEnqueue(() => downloadItem.Status = "Validating depots and keys...");

                var depotKeys = new Dictionary<string, byte[]>();
                var readyDepots = new List<DepotInfo>();
                var skippedDepots = new List<string>();

                foreach (var depot in selectedDepots)
                {
                    cancellation.Token.ThrowIfCancellationRequested();

                    string? manifestPath = ResolveManifestPath(depot);
                    if (manifestPath is null)
                    {
                        skippedDepots.Add(depot.DepotId);
                        AppLog.Write($"[Downloads] Depot {depot.DepotId}: missing local .manifest (skipped)");
                        continue;
                    }

                    if (string.IsNullOrEmpty(depot.HexKey))
                        throw new Exception($"Missing HexKey for Depot {depot.DepotId}");

                    if (depot.HexKey.Length % 2 != 0)
                        throw new Exception($"Invalid HexKey length for Depot {depot.DepotId}: {depot.HexKey}");

                    depotKeys[depot.DepotId] = Convert.FromHexString(depot.HexKey);
                    depot.ManifestPath = manifestPath;
                    readyDepots.Add(depot);
                    AppLog.Write(
                        $"[Downloads] Depot {depot.DepotId}: ready keyLen={depot.HexKey.Length / 2} " +
                        $"manifest={manifestPath}");
                }

                if (readyDepots.Count == 0)
                    throw new Exception("No selected depots have a local .manifest file to download.");

                if (skippedDepots.Count > 0)
                {
                    AppLog.Write($"[Downloads] Skipped depots without manifest: {string.Join(", ", skippedDepots)}");
                    DispatcherQueue.TryEnqueue(() =>
                        _notifications.Show(
                            "Some depots skipped",
                            $"Missing manifest files for depot(s): {string.Join(", ", skippedDepots)}",
                            InfoBarSeverity.Warning));
                }

                downloadDest = await _installPathService.GetInstallDirectoryAsync(
                    cancelledGameName,
                    cancelledAppId,
                    promptIfMissing: false);
                Directory.CreateDirectory(downloadDest);
                AppLog.Write($"[Downloads] Install directory: {downloadDest}");
                await _gameLibrary.UpsertAsync(new GameEntry
                {
                    AppId = cancelledAppId,
                    Name = cancelledGameName,
                    Image = cancelledCoverArt,
                    InstallPath = downloadDest,
                    IsInstalled = false
                });

                DispatcherQueue.TryEnqueue(() => downloadItem.Status = "Preparing install files...");

                int cdnCellId = await _settingsService.GetCdnCellIdAsync();
                int maxConcurrentChunks = await _settingsService.GetMaxConcurrentChunksAsync();
                AppLog.Write(
                    $"[Downloads] Engine settings cellId={cdnCellId} concurrency={maxConcurrentChunks} " +
                    $"readyDepots={readyDepots.Count}");
                await GameDownload.BatchEngineStart(
                    readyDepots,
                    depotKeys,
                    downloadDest,
                    progressReporter,
                    pause.WaitWhilePausedAsync,
                    cancellation.Token,
                    cdnCellId,
                    maxConcurrentChunks);
                downloadCompleted = true;
                AppLog.Write($"[Downloads] Completed '{cancelledGameName}' → {downloadDest}");

                await _gameLibrary.UpsertAsync(new GameEntry
                {
                    AppId = cancelledAppId,
                    Name = cancelledGameName,
                    Image = cancelledCoverArt,
                    InstallPath = downloadDest,
                    IsInstalled = true
                });
                RequestLibraryRefresh();
                await _windowsToast.NotifyInstallCompleteAsync(
                    cancelledGameName,
                    cancelledAppId,
                    cancelledCoverArt);

                if (executePostDownload)
                {
                    DispatcherQueue.TryEnqueue(() => downloadItem.Status = "Removing Steam DRM - SteamAutoCrack in progress...");
                    await _postDownloadService.RunPostDownloadCommandAsync(
                        cancelledGameName,
                        cancelledAppId,
                        downloadDest,
                        cancellation.Token);
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    downloadItem.ProgressValue = 100;
                    downloadItem.Status = "Download complete";
                });

                DispatcherQueue.TryEnqueue(() =>
                {
                    _notifications.Show("Success", $"{cancelledGameName} finished downloading.", InfoBarSeverity.Success);
                    Downloads.Remove(downloadItem);
                });
            }
            catch (Exception ex) when (IsCancellation(ex, cancellation))
            {
                AppLog.Write(
                    $"[Downloads] Cancelled '{cancelledGameName}' appId={cancelledAppId} dest={downloadDest ?? "(unset)"}");
                await CleanupCancelledDownloadAsync(
                    cancelledAppId,
                    cancelledGameName,
                    cancelledCoverArt,
                    downloadDest,
                    preserveLibraryOnCancel,
                    wasInstalledBeforeDownload,
                    installPathBeforeDownload);

                DispatcherQueue.TryEnqueue(() =>
                {
                    downloadItem.Status = "Cancelled";
                    Downloads.Remove(downloadItem);
                    _notifications.Show("Cancelled", $"{cancelledGameName} download was cancelled.", InfoBarSeverity.Informational);
                });
            }
            catch (Exception ex)
            {
                // Near-100% progress must not hide a real failure (last-chunk CDN hang/timeout).
                string rootMessage = AppLog.GetRootMessage(ex);
                AppLog.Write(ex, "Download critical error");
                AppLog.Write(
                    $"[Downloads] Failed '{cancelledGameName}' appId={cancelledAppId} " +
                    $"completedFlag={downloadCompleted} dest={downloadDest ?? "(unset)"}");

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (downloadCompleted)
                    {
                        downloadItem.ProgressValue = 100;
                        downloadItem.Status = "Download complete";
                        return;
                    }

                    downloadItem.Status = TruncateStatus($"Error: {rootMessage}");
                    _notifications.Show("Error", rootMessage, InfoBarSeverity.Error);
                });
            }
            finally
            {
                try
                {
                    cancellation.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        });

        await Task.CompletedTask;
    }

    private async Task CleanupCancelledDownloadAsync(
        string appId,
        string gameName,
        string coverArt,
        string? downloadDest,
        bool preserveLibrary,
        bool wasInstalledBefore,
        string installPathBefore)
    {
        try
        {
            AppLog.Write(
                $"[Downloads] Cleanup after cancel appId={appId} game='{gameName}' " +
                $"dest={downloadDest ?? "(unset)"} preserveLibrary={preserveLibrary} " +
                $"wasInstalledBefore={wasInstalledBefore}");

            string installPath = downloadDest ?? string.Empty;
            if (!preserveLibrary && string.IsNullOrWhiteSpace(installPath))
            {
                try
                {
                    installPath = await _installPathService.GetInstallDirectoryAsync(
                        gameName ?? string.Empty,
                        appId,
                        promptIfMissing: false);
                }
                catch (Exception ex)
                {
                    AppLog.Write(ex, "Cleanup resolve install path");
                    installPath = string.Empty;
                }
            }

            bool isPriorInstallFolder = wasInstalledBefore
                && !string.IsNullOrWhiteSpace(installPath)
                && !string.IsNullOrWhiteSpace(installPathBefore)
                && string.Equals(
                    Path.GetFullPath(installPath),
                    Path.GetFullPath(installPathBefore),
                    StringComparison.OrdinalIgnoreCase);

            // Delete this session's partial download folder, but never wipe a prior install.
            if (!string.IsNullOrWhiteSpace(installPath)
                && Directory.Exists(installPath)
                && !isPriorInstallFolder)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        AppLog.Write($"[Downloads] Uninstall cleanup attempt {attempt + 1}/5 path={installPath}");
                        await _uninstallService.UninstallAsync(
                            new GameEntry
                            {
                                AppId = appId,
                                Name = gameName ?? string.Empty,
                                InstallPath = installPath
                            },
                            removeFromLibrary: false);
                        AppLog.Write("[Downloads] Uninstall cleanup succeeded");
                        break;
                    }
                    catch (IOException ex) when (attempt < 4)
                    {
                        AppLog.Write($"[Downloads] Cleanup IO retry: {ex.Message}");
                        await Task.Delay(250);
                    }
                    catch (UnauthorizedAccessException ex) when (attempt < 4)
                    {
                        AppLog.Write($"[Downloads] Cleanup access retry: {ex.Message}");
                        await Task.Delay(250);
                    }
                }
            }
            else if (isPriorInstallFolder)
            {
                AppLog.Write(
                    $"[Downloads] Cleanup: leaving prior install folder untouched path={installPath}");
            }

            if (preserveLibrary)
            {
                // Restore pre-download library state so library-only titles show Install again
                // (not Play from a leftover InstallPath / empty folder migration).
                await _gameLibrary.UpsertAsync(new GameEntry
                {
                    AppId = appId,
                    Name = string.IsNullOrWhiteSpace(gameName) ? $"Steam App {appId}" : gameName,
                    Image = coverArt,
                    StartLocation = string.Empty,
                    InstallPath = wasInstalledBefore ? installPathBefore : string.Empty,
                    IsInstalled = wasInstalledBefore
                });
                AppLog.Write(
                    $"[Downloads] Cleanup: restored library entry appId={appId} " +
                    $"IsInstalled={wasInstalledBefore}");
            }
            else if (!string.IsNullOrWhiteSpace(appId))
            {
                AppLog.Write($"[Downloads] Cleanup: removing library entry appId={appId}");
                await _gameLibrary.RemoveAsync(appId);
            }

            RequestLibraryRefresh();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Cancel cleanup failed");
            try
            {
                if (preserveLibrary)
                {
                    await _gameLibrary.UpsertAsync(new GameEntry
                    {
                        AppId = appId,
                        Name = string.IsNullOrWhiteSpace(gameName) ? $"Steam App {appId}" : gameName,
                        Image = coverArt,
                        StartLocation = string.Empty,
                        InstallPath = wasInstalledBefore ? installPathBefore : string.Empty,
                        IsInstalled = wasInstalledBefore
                    });
                }
                else if (!string.IsNullOrWhiteSpace(appId))
                {
                    await _gameLibrary.RemoveAsync(appId);
                }
            }
            catch (Exception removeEx)
            {
                AppLog.Write(removeEx, "Cancel cleanup library update failed");
            }

            RequestLibraryRefresh();
        }
    }

    private void RequestLibraryRefresh()
    {
        if (!DispatcherQueue.TryEnqueue(RefreshService.RequestRefresh))
            RefreshService.RequestRefresh();
    }

    private static bool IsCancellation(Exception ex, CancellationTokenSource cancellation)
    {
        bool cancelRequested;
        try
        {
            cancelRequested = cancellation.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            cancelRequested = true;
        }

        return ex is OperationCanceledException or TaskCanceledException ||
               (ex is ObjectDisposedException && cancelRequested) ||
               (ex is AggregateException aggregate && aggregate.InnerExceptions.All(inner =>
                   inner is OperationCanceledException or TaskCanceledException ||
                   (inner is ObjectDisposedException && cancelRequested)));
    }

    private static string TruncateStatus(string status, int maxLength = 160)
    {
        if (string.IsNullOrEmpty(status) || status.Length <= maxLength)
            return status;
        return status[..(maxLength - 1)] + "…";
    }

    private static ImageSource? LoadImage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return null;

        return new BitmapImage(new Uri(Path.GetFullPath(imagePath), UriKind.Absolute));
    }
}
