using System.Collections.ObjectModel;
using EZManifest.Models;
using EZManifest.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using WinRT.Interop;
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

    private string _finalPath = string.Empty;
    private string _appId = string.Empty;
    private string _currentGameName = string.Empty;
    private string _currentCoverArtPath = string.Empty;
    private string _currentLogoPath = string.Empty;

    public ObservableCollection<DownloadItem> Downloads { get; } = new();

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
        WindowProvider windowProvider)
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

        InitializeComponent();
    }

    private async void BrowseManifest_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _windowProvider.GetWindowHandle());
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add(".zip");

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        try
        {
            await ExtractZipAsync(file.Path);
        }
        catch (Exception ex)
        {
            _notifications.Show("Import failed", ex.Message, InfoBarSeverity.Error);
            Debug.WriteLine($"[MANIFEST IMPORT ERROR]: {ex}");
        }
    }

    private async Task ExtractZipAsync(string zipPath)
    {
        var archive = await _archiveService.ExtractAsync(zipPath);
        _finalPath = archive.ExtractionDirectory;
        _appId = archive.AppId;
        _currentGameName = $"Steam App {_appId}";
        _currentLogoPath = archive.LogoPath;
        _currentCoverArtPath = archive.CoverArtPath;

        await _steamMetadata.DownloadArtworkAsync(_appId, archive.LogoPath, archive.CoverArtPath);
        await AddSteamGameToLibraryAsync(_appId, archive.CoverArtPath);
        await ManifestDepotIdChoiceAsync(archive.LuaFilePath);
    }

    private async Task AddSteamGameToLibraryAsync(string appId, string coverArt)
    {
        try
        {
            string gameName = await _steamMetadata.GetGameNameAsync(appId);
            _currentGameName = gameName;
            string installPath = await _installPathService.GetInstallDirectoryAsync(gameName, appId);
            await _gameLibrary.UpsertAsync(new GameEntry
            {
                AppId = appId,
                Name = gameName,
                Image = coverArt,
                StartLocation = string.Empty,
                InstallPath = installPath
            });

            RefreshService.RequestRefresh();
            _notifications.Show("Success", "Game added successfully!", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error: {ex.Message}");
        }
    }

    private async Task ManifestDepotIdChoiceAsync(string luaFilePath)
    {
        var availableItems = _manifestParser.Parse(luaFilePath);
        var metadata = await _depotMetadata.GetDepotMetadataAsync(_appId);
        var displayRows = BuildDepotDisplayRows(availableItems, metadata);

        var root = new Grid
        {
            RowSpacing = 10,
            MinWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBlock = new TextBlock
        {
            Text = _currentGameName,
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(titleBlock, 0);
        root.Children.Add(titleBlock);

        var appIdBlock = new TextBlock
        {
            Text = $"App ID {_appId}",
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetRow(appIdBlock, 1);
        root.Children.Add(appIdBlock);

        // One shared Grid so header + rows share identical column widths.
        var table = new Grid
        {
            ColumnSpacing = 16,
            RowSpacing = 4,
            MinWidth = 600,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

        void AddHeaderCell(string text, int column, TextAlignment align = TextAlignment.Left)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.7,
                TextAlignment = align,
                HorizontalAlignment = align == TextAlignment.Right
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(block, 0);
            Grid.SetColumn(block, column);
            table.Children.Add(block);
        }

        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddHeaderCell("App ID", 1);
        AddHeaderCell("Manifest ID", 2);
        AddHeaderCell("DL size", 3, TextAlignment.Right);

        var checkBoxes = new List<CheckBox>();
        for (int i = 0; i < displayRows.Count; i++)
        {
            int rowIndex = i + 1;
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = displayRows[i];

            var checkBox = new CheckBox
            {
                Tag = row.Depot,
                IsChecked = row.Display.HasLocalManifest,
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

            // Full-row hit target added first so checkbox/text sit above it.
            var hitTarget = new Border
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            Grid.SetRow(hitTarget, rowIndex);
            Grid.SetColumn(hitTarget, 0);
            Grid.SetColumnSpan(hitTarget, 4);
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
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            Grid.SetRow(appIdText, rowIndex);
            Grid.SetColumn(appIdText, 1);
            table.Children.Add(appIdText);

            var manifestText = new TextBlock
            {
                Text = row.Display.ManifestId,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            };
            Grid.SetRow(manifestText, rowIndex);
            Grid.SetColumn(manifestText, 2);
            table.Children.Add(manifestText);

            var sizeText = new TextBlock
            {
                Text = row.Display.DownloadText,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
                IsHitTestVisible = false
            };
            Grid.SetRow(sizeText, rowIndex);
            Grid.SetColumn(sizeText, 3);
            table.Children.Add(sizeText);
        }

        var scrollViewer = new ScrollViewer
        {
            Content = table,
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto
        };
        Grid.SetRow(scrollViewer, 2);
        root.Children.Add(scrollViewer);

        var dialog = new ContentDialog
        {
            Title = "Select depots",
            PrimaryButtonText = "Download Selected",
            CloseButtonText = "Cancel",
            Content = root,
            XamlRoot = XamlRoot
        };
        // Per-dialog override (App.xaml also sets these via XamlControlsResources).
        dialog.Resources["ContentDialogMaxWidth"] = 900.0;
        dialog.Resources["ContentDialogMinWidth"] = 640.0;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var selectedItems = checkBoxes
                .Where(cb => cb.IsChecked == true)
                .Select(cb => (DepotInfo)cb.Tag)
                .ToList();

            if (selectedItems.Count == 0)
            {
                await _gameLibrary.RemoveAsync(_appId);
                RefreshService.RequestRefresh();
                return;
            }

            await StartDownloadProcessAsync(selectedItems);
            RefreshService.RequestRefresh();
        }
        else
        {
            await _gameLibrary.RemoveAsync(_appId);
            RefreshService.RequestRefresh();
        }
    }

    private List<(DepotInfo Depot, DepotDisplayInfo Display)> BuildDepotDisplayRows(
        IReadOnlyList<DepotInfo> depots,
        IReadOnlyDictionary<string, DepotMetadata> metadata)
    {
        var rows = new List<(DepotInfo Depot, DepotDisplayInfo Display)>();
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

            rows.Add((depot, new DepotDisplayInfo
            {
                DepotId = depot.DepotId,
                ManifestId = depot.ManifestId,
                Configuration = meta?.Configuration ?? string.Empty,
                SizeBytes = size,
                DownloadBytes = download,
                HasLocalManifest = hasManifest
            }));
        }

        return rows;
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

    private async Task StartDownloadProcessAsync(List<DepotInfo> selectedDepots)
    {
        var cancellation = new CancellationTokenSource();
        var pause = new DownloadPauseState();
        var downloadItem = new DownloadItem
        {
            GameName = _currentGameName,
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
            try
            {
                if (!cancellation.IsCancellationRequested)
                    cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Download task already finished and disposed the token source.
            }
        });
        downloadItem.PauseCommand = new RelayCommand(() =>
        {
            if (cancelRequested)
                return;

            if (pause.Toggle())
            {
                downloadItem.Status = "Paused";
                downloadItem.PauseButtonText = "Resume";
            }
            else
            {
                downloadItem.Status = "Downloading game files...";
                downloadItem.PauseButtonText = "Pause";
            }
        });

        Downloads.Add(downloadItem);

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
                        continue;
                    }

                    if (string.IsNullOrEmpty(depot.HexKey))
                        throw new Exception($"Missing HexKey for Depot {depot.DepotId}");

                    if (depot.HexKey.Length % 2 != 0)
                        throw new Exception($"Invalid HexKey length for Depot {depot.DepotId}: {depot.HexKey}");

                    depotKeys[depot.DepotId] = Convert.FromHexString(depot.HexKey);
                    depot.ManifestPath = manifestPath;
                    readyDepots.Add(depot);
                }

                if (readyDepots.Count == 0)
                    throw new Exception("No selected depots have a local .manifest file to download.");

                if (skippedDepots.Count > 0)
                {
                    DispatcherQueue.TryEnqueue(() =>
                        _notifications.Show(
                            "Some depots skipped",
                            $"Missing manifest files for depot(s): {string.Join(", ", skippedDepots)}",
                            InfoBarSeverity.Warning));
                }

                downloadDest = await _installPathService.GetInstallDirectoryAsync(cancelledGameName, cancelledAppId);
                Directory.CreateDirectory(downloadDest);
                await _gameLibrary.UpsertAsync(new GameEntry
                {
                    AppId = cancelledAppId,
                    Name = cancelledGameName,
                    Image = cancelledCoverArt,
                    InstallPath = downloadDest
                });

                DispatcherQueue.TryEnqueue(() => downloadItem.Status = "Downloading game files...");

                int cdnCellId = await _settingsService.GetCdnCellIdAsync();
                await GameDownload.BatchEngineStart(
                    readyDepots,
                    depotKeys,
                    downloadDest,
                    progressReporter,
                    pause.WaitWhilePausedAsync,
                    cancellation.Token,
                    cdnCellId);
                downloadCompleted = true;

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
                await CleanupCancelledDownloadAsync(cancelledAppId, cancelledGameName, downloadDest);

                DispatcherQueue.TryEnqueue(() =>
                {
                    downloadItem.Status = "Cancelled";
                    Downloads.Remove(downloadItem);
                    _notifications.Show("Cancelled", $"{cancelledGameName} download was cancelled.", InfoBarSeverity.Informational);
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (downloadCompleted || downloadItem.ProgressValue >= 100)
                    {
                        downloadItem.ProgressValue = 100;
                        downloadItem.Status = "Download complete";
                    }
                    else
                    {
                        downloadItem.Status = "Critical error";
                        _notifications.Show("Error", ex.Message, InfoBarSeverity.Error);
                    }
                });
                Debug.WriteLine($"[CRASH TRACE]: {ex}");
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

    private async Task CleanupCancelledDownloadAsync(string appId, string gameName, string? downloadDest)
    {
        try
        {
            string installPath = downloadDest ?? string.Empty;
            if (string.IsNullOrWhiteSpace(installPath))
            {
                try
                {
                    installPath = await _installPathService.GetInstallDirectoryAsync(gameName, appId);
                }
                catch
                {
                    installPath = string.Empty;
                }
            }

            // Retry briefly — cancel can leave file handles open for a moment.
            if (!string.IsNullOrWhiteSpace(installPath) && Directory.Exists(installPath))
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        await _uninstallService.UninstallAsync(new GameEntry
                        {
                            AppId = appId,
                            Name = gameName,
                            InstallPath = installPath
                        });
                        break;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        await Task.Delay(250);
                    }
                    catch (UnauthorizedAccessException) when (attempt < 4)
                    {
                        await Task.Delay(250);
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(appId))
            {
                await _gameLibrary.RemoveAsync(appId);
            }

            RequestLibraryRefresh();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CANCEL CLEANUP]: {ex}");
            try
            {
                if (!string.IsNullOrWhiteSpace(appId))
                    await _gameLibrary.RemoveAsync(appId);
            }
            catch
            {
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

    private static ImageSource? LoadImage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return null;

        return new BitmapImage(new Uri(Path.GetFullPath(imagePath), UriKind.Absolute));
    }
}
