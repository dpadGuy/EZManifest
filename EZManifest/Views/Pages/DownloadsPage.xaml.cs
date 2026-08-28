using System.Collections.ObjectModel;
using System.Diagnostics;
using EZManifest.Models;
using EZManifest.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
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
    private readonly PostDownloadService _postDownloadService;

    private string _finalPath = string.Empty;
    private string _appId = string.Empty;
    private string _currentGameName = string.Empty;
    private string _currentCoverArtPath = string.Empty;
    private string _currentLogoPath = string.Empty;
    private bool _dropHighlight;
    private int _dragDepth;
    private int _importBusy;
    private bool _preserveLibraryOnCancel;

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
        WindowProvider windowProvider,
        PostDownloadService postDownloadService)
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

        InitializeComponent();
        Downloads.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        bool empty = Downloads.Count == 0;
        EmptyDropPrompt.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        BrowseActivePanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void BrowseManifestForDownload_Click(object sender, RoutedEventArgs e)
    {
        var kind = await PromptZipOrFolderAsync(
            "Browse for download",
            "Choose .zip file or folder.");
        if (kind is null)
            return;

        string? path = kind == ManifestPickKind.Zip
            ? await PickZipFileAsync()
            : await PickManifestFolderAsync();

        if (string.IsNullOrWhiteSpace(path))
            return;

        await ImportManifestAsync(path);
    }

    private async void BrowseManifestForLibrary_Click(object sender, RoutedEventArgs e)
    {
        var kind = await PromptZipOrFolderAsync(
            "Browse for library adding",
            "Choose .zip file or folder.");
        if (kind is null)
            return;

        if (kind == ManifestPickKind.Zip)
        {
            IReadOnlyList<string>? paths = await PickZipFilesAsync();
            if (paths is null || paths.Count == 0)
                return;

            await ImportManifestsToLibraryAsync(paths);
            return;
        }

        string? folder = await PickManifestFolderAsync();
        if (string.IsNullOrWhiteSpace(folder))
            return;

        await ImportManifestsToLibraryAsync(new[] { folder });
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
            PrimaryButtonText = ".zip file",
            SecondaryButtonText = "Folder",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
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

    private async Task<string?> PickZipFileAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _windowProvider.GetWindowHandle());
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add(".zip");

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async Task<IReadOnlyList<string>?> PickZipFilesAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _windowProvider.GetWindowHandle());
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add(".zip");

        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0)
            return null;

        return files.Select(file => file.Path).ToList();
    }

    private async Task<string?> PickManifestFolderAsync()
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, _windowProvider.GetWindowHandle());
        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async Task ImportManifestsToLibraryAsync(IReadOnlyList<string> paths)
    {
        if (Interlocked.CompareExchange(ref _importBusy, 1, 0) != 0)
        {
            AppLog.Write("[Downloads] Library bulk import ignored (already in progress)");
            return;
        }

        var statusText = new TextBlock
        {
            Text = "Preparing...",
            TextWrapping = TextWrapping.WrapWholeWords
        };
        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = Math.Max(1, paths.Count),
            Value = 0,
            Height = 8
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(statusText);
        content.Children.Add(progressBar);

        var dialog = new ContentDialog
        {
            Title = "Adding to library",
            Content = content,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            CloseButtonText = "Please wait..."
        };
        dialog.Resources["ContentDialogMinWidth"] = 420.0;
        dialog.Resources["ContentDialogMaxWidth"] = 560.0;

        bool finished = false;
        dialog.Closing += (_, args) =>
        {
            if (!finished)
                args.Cancel = true;
        };

        var showTask = dialog.ShowAsync().AsTask();

        int added = 0;
        var failures = new List<string>();

        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                string displayName = GetManifestDisplayName(path);
                statusText.Text = $"Adding {displayName}\n({i + 1} of {paths.Count})";

                try
                {
                    await AddManifestToLibraryOnlyAsync(path);
                    added++;
                }
                catch (Exception ex)
                {
                    AppLog.Write(ex, $"Library add failed for {path}");
                    failures.Add($"{displayName}: {ex.Message}");
                }

                progressBar.Value = i + 1;
            }

            statusText.Text = failures.Count == 0
                ? $"Added {added} game(s) to the library."
                : $"Added {added} of {paths.Count}. {failures.Count} failed.";
            if (failures.Count > 0 && failures.Count <= 5)
                statusText.Text += "\n\n" + string.Join("\n", failures);

            finished = true;
            dialog.CloseButtonText = "OK";
            await showTask;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Library bulk import failed");
            finished = true;
            try
            {
                dialog.Hide();
            }
            catch
            {
            }

            _notifications.Show("Library import failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _importBusy, 0);
            RefreshService.RequestRefresh();
        }

        if (added > 0)
        {
            _notifications.Show(
                "Library updated",
                failures.Count == 0
                    ? $"Added {added} game(s)."
                    : $"Added {added}; {failures.Count} failed.",
                failures.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
    }

    private async Task AddManifestToLibraryOnlyAsync(string path)
    {
        AppLog.Write($"[Downloads] Library-only import: {path}");
        var archive = await _archiveService.ExtractAsync(path);

        if (IsAppCurrentlyDownloading(archive.AppId))
            throw new InvalidOperationException($"App {archive.AppId} is already downloading.");

        await _steamMetadata.DownloadArtworkAsync(archive.AppId, archive.LogoPath, archive.CoverArtPath);
        string gameName = await _steamMetadata.GetGameNameAsync(archive.AppId);

        await _gameLibrary.UpsertAsync(new GameEntry
        {
            AppId = archive.AppId,
            Name = gameName,
            Image = archive.CoverArtPath,
            StartLocation = string.Empty,
            InstallPath = string.Empty,
            IsInstalled = false
        });

        AppLog.Write($"[Downloads] Library-only upserted appId={archive.AppId} name='{gameName}'");
    }

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

        AppLog.Write($"[Downloads] Install from library appId={_appId} dir={_finalPath}");
        await ManifestDepotIdChoiceAsync(luaPath, removeFromLibraryOnCancel: false);
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

        if (paths.Count == 1)
        {
            var choice = await _messageBoxService.ShowAsync(
                "Import manifest",
                $"Choose how to import \"{GetManifestDisplayName(paths[0])}\".",
                "Download",
                "Add to library");

            if (choice == ContentDialogResult.Primary)
                await ImportManifestAsync(paths[0]);
            else
                await ImportManifestsToLibraryAsync(paths);

            return;
        }

        await ImportManifestsToLibraryAsync(paths);
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

    private static string GetManifestDisplayName(string path)
    {
        if (Directory.Exists(path))
        {
            string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }

        return Path.GetFileName(path);
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

        await _steamMetadata.DownloadArtworkAsync(_appId, archive.LogoPath, archive.CoverArtPath);
        _preserveLibraryOnCancel = false;
        await AddSteamGameToLibraryAsync(_appId, archive.CoverArtPath, isInstalled: false);
        await ManifestDepotIdChoiceAsync(archive.LuaFilePath, removeFromLibraryOnCancel: true);
    }

    private bool IsAppCurrentlyDownloading(string appId) =>
        !string.IsNullOrWhiteSpace(appId) &&
        Downloads.Any(d => string.Equals(d.AppId, appId, StringComparison.OrdinalIgnoreCase));

    private async Task AddSteamGameToLibraryAsync(string appId, string coverArt, bool isInstalled)
    {
        try
        {
            string gameName = await _steamMetadata.GetGameNameAsync(appId);
            _currentGameName = gameName;
            AppLog.Write($"[Downloads] Resolved game name appId={appId} → '{gameName}'");
            string installPath = isInstalled
                ? await _installPathService.GetInstallDirectoryAsync(gameName, appId)
                : string.Empty;
            await _gameLibrary.UpsertAsync(new GameEntry
            {
                AppId = appId,
                Name = gameName,
                Image = coverArt,
                StartLocation = string.Empty,
                InstallPath = installPath,
                IsInstalled = isInstalled
            });

            RefreshService.RequestRefresh();
            _notifications.Show("Success", "Game added successfully!", InfoBarSeverity.Success);
            AppLog.Write($"[Downloads] Library upserted installPath={installPath} isInstalled={isInstalled}");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "AddSteamGameToLibrary failed");
        }
    }

    private async Task ManifestDepotIdChoiceAsync(string luaFilePath, bool removeFromLibraryOnCancel)
    {
        var availableItems = _manifestParser.Parse(luaFilePath);
        var metadata = await _depotMetadata.GetDepotMetadataAsync(_appId);
        var displayRows = BuildDepotDisplayRows(availableItems, metadata);

        var root = new Grid
        {
            RowSpacing = 10,
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
            TextWrapping = TextWrapping.NoWrap
        };
        Grid.SetRow(titleBlock, 0);
        root.Children.Add(titleBlock);

        var appIdRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(0, 0, 0, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        appIdRow.Children.Add(new TextBlock
        {
            Text = $"App ID {_appId}",
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center
        });
        appIdRow.Children.Add(new HyperlinkButton
        {
            Content = "SteamDB depots",
            NavigateUri = new Uri($"https://steamdb.info/app/{_appId}/depots/"),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        });
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
        appIdRow.Children.Add(executePostDownloadCheck);
        Grid.SetRow(appIdRow, 1);
        root.Children.Add(appIdRow);

        // [check] [App ID *] [Manifest *] [DL size *] — equal category columns, centered text.
        var table = new Grid
        {
            ColumnSpacing = 16,
            RowSpacing = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 16, 0)
        };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 80 });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.4, GridUnitType.Star), MinWidth = 160 });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 80 });

        void AddHeaderCell(string text, int column)
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.7,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(block, 0);
            Grid.SetColumn(block, column);
            table.Children.Add(block);
        }

        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddHeaderCell("App ID", 1);
        AddHeaderCell("Manifest ID", 2);
        AddHeaderCell("DL size", 3);

        var checkBoxes = new List<CheckBox>();
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
        uncheckAllButton.Click += (_, _) =>
        {
            foreach (CheckBox box in checkBoxes)
            {
                if (box.IsEnabled)
                    box.IsChecked = false;
            }
        };

        // Match CheckBox layout: 32×32 hit area, glyph left-aligned inside it.
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
        table.Children.Add(uncheckHost);
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
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            Grid.SetRow(appIdText, rowIndex);
            Grid.SetColumn(appIdText, 1);
            table.Children.Add(appIdText);

            var manifestText = new TextBlock
            {
                Text = row.Display.ManifestId,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
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
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
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
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 0, 8, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled
        };
        Grid.SetRow(scrollViewer, 2);
        root.Children.Add(scrollViewer);

        var dialog = new ContentDialog
        {
            Title = "Select depots",
            PrimaryButtonText = "Download Selected",
            CloseButtonText = "Cancel",
            Content = root,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
        };
        // Width follows the title; table stretches to that full width.
        dialog.Resources["ContentDialogMinWidth"] = 320.0;
        dialog.Resources["ContentDialogMaxWidth"] = 900.0;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var selectedItems = checkBoxes
                .Where(cb => cb.IsChecked == true)
                .Select(cb => (DepotInfo)cb.Tag)
                .ToList();

            if (selectedItems.Count == 0)
            {
                if (removeFromLibraryOnCancel)
                {
                    await _gameLibrary.RemoveAsync(_appId);
                    RefreshService.RequestRefresh();
                }
                return;
            }

            await StartDownloadProcessAsync(
                selectedItems,
                executePostDownload: executePostDownloadCheck.IsChecked == true);
            RefreshService.RequestRefresh();
        }
        else if (removeFromLibraryOnCancel)
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

    private async Task StartDownloadProcessAsync(List<DepotInfo> selectedDepots, bool executePostDownload)
    {
        if (IsAppCurrentlyDownloading(_appId))
        {
            await _messageBoxService.ShowAsync(
                "Already downloading",
                $"\"{_currentGameName}\" is currently in the download process and cannot be added again.");
            return;
        }

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
                downloadItem.Status = "Paused";
                downloadItem.PauseButtonText = "Resume";
                AppLog.Write($"[Downloads] Paused '{_currentGameName}'");
            }
            else
            {
                downloadItem.Status = "Downloading game files...";
                downloadItem.PauseButtonText = "Pause";
                AppLog.Write($"[Downloads] Resumed '{_currentGameName}'");
            }
        });

        Downloads.Add(downloadItem);
        bool preserveLibraryOnCancel = _preserveLibraryOnCancel;
        AppLog.Write(
            $"[Downloads] Queued download '{_currentGameName}' appId={_appId} " +
            $"selectedDepots={selectedDepots.Count} ids=[{string.Join(", ", selectedDepots.Select(d => d.DepotId))}] " +
            $"preserveLibraryOnCancel={preserveLibraryOnCancel} executePostDownload={executePostDownload}");

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

                downloadDest = await _installPathService.GetInstallDirectoryAsync(cancelledGameName, cancelledAppId);
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
                    preserveLibraryOnCancel);

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
        bool preserveLibrary)
    {
        try
        {
            AppLog.Write(
                $"[Downloads] Cleanup after cancel appId={appId} game='{gameName}' " +
                $"dest={downloadDest ?? "(unset)"} preserveLibrary={preserveLibrary}");
            string installPath = downloadDest ?? string.Empty;
            if (string.IsNullOrWhiteSpace(installPath))
            {
                try
                {
                    installPath = await _installPathService.GetInstallDirectoryAsync(gameName, appId);
                }
                catch (Exception ex)
                {
                    AppLog.Write(ex, "Cleanup resolve install path");
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
                        AppLog.Write($"[Downloads] Uninstall cleanup attempt {attempt + 1}/5 path={installPath}");
                        await _uninstallService.UninstallAsync(
                            new GameEntry
                            {
                                AppId = appId,
                                Name = gameName,
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

            if (preserveLibrary)
            {
                await _gameLibrary.UpsertAsync(new GameEntry
                {
                    AppId = appId,
                    Name = gameName,
                    Image = coverArt,
                    StartLocation = string.Empty,
                    InstallPath = string.Empty,
                    IsInstalled = false
                });
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
                        Name = gameName,
                        Image = coverArt,
                        StartLocation = string.Empty,
                        InstallPath = string.Empty,
                        IsInstalled = false
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
