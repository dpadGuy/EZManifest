using System.Collections.ObjectModel;
using System.Diagnostics;
using EZManifest.Models;
using EZManifest.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace EZManifest.Views.Pages;

public sealed partial class LibraryPage : Page
{
    private readonly GameLibraryService _gameLibrary;
    private readonly GameUninstallService _uninstallService;
    private readonly AppMessageBoxService _messageBoxService;
    private readonly GameInstallPathService _installPathService;
    private readonly GoldbergPatchService _goldbergPatchService;
    private readonly AppNavigationService _navigation;
    private readonly IServiceProvider _services;
    private readonly HashSet<string> _selectedAppIds = new(StringComparer.OrdinalIgnoreCase);

    private bool _hasLoaded;
    private bool _refreshPending;
    private int _loadVersion;
    private Task? _loadTask;
    private string _searchQuery = string.Empty;
    private int _selectionAnchorIndex = -1;

    public ObservableCollection<GameEntry> AppsList { get; } = new();
    public ObservableCollection<GameEntry> FilteredApps { get; } = new();

    public LibraryPage(
        GameLibraryService gameLibrary,
        GameUninstallService uninstallService,
        AppMessageBoxService messageBoxService,
        GameInstallPathService installPathService,
        GoldbergPatchService goldbergPatchService,
        AppNavigationService navigation,
        IServiceProvider services)
    {
        _gameLibrary = gameLibrary;
        _uninstallService = uninstallService;
        _messageBoxService = messageBoxService;
        _installPathService = installPathService;
        _goldbergPatchService = goldbergPatchService;
        _navigation = navigation;
        _services = services;

        InitializeComponent();
        Loaded += LibraryPage_Loaded;
        RefreshService.OnListRefreshRequested += HandleRefresh;
    }

    private void LibraryPage_Loaded(object sender, RoutedEventArgs e)
    {
        // Avoid the double hitch: only load on first show, or when a refresh was requested
        // while we were on another page.
        if (!_hasLoaded || _refreshPending)
            _ = EnsureLoadedAsync(force: _refreshPending);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        // Keep RefreshService subscription: this page is a singleton and must
        // still refresh while another nav page is visible.
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
            ApplyFilter(_searchQuery);
            _hasLoaded = true;
            _refreshPending = false;
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
        ApplyFilter(_searchQuery);
    }

    private void ApplyFilter(string? query)
    {
        IEnumerable<GameEntry> source = AppsList;
        if (!string.IsNullOrWhiteSpace(query))
        {
            source = AppsList.Where(game =>
                game.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                game.AppId.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = source.ToList();
        if (CollectionsEqual(FilteredApps, filtered))
            return;

        ClearSelection();
        FilteredApps.Clear();
        foreach (var game in filtered)
            FilteredApps.Add(game);
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

    private static bool EntriesEqual(GameEntry left, GameEntry right) =>
        string.Equals(left.AppId, right.AppId, StringComparison.Ordinal) &&
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        string.Equals(left.Image, right.Image, StringComparison.Ordinal) &&
        string.Equals(left.StartLocation, right.StartLocation, StringComparison.Ordinal) &&
        string.Equals(left.InstallPath, right.InstallPath, StringComparison.Ordinal) &&
        left.IsInstalled == right.IsInstalled;

    private async void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetGameEntry(sender) is not GameEntry game)
            return;

        await RunPrimaryActionAsync(game);
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
        if (!game.IsInstalled)
        {
            await InstallGameAsync(game);
            return;
        }

        await PlayGameAsync(game);
    }

    private void CoverArt_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border cover)
            return;

        // Steam vertical covers are 300×450 → keep 2:3 as the card width changes.
        double width = e.NewSize.Width;
        if (width <= 0)
            return;

        double targetHeight = width * (450.0 / 300.0);
        // Default Height is NaN (Auto); Abs(NaN - x) is NaN, so the old check never applied.
        if (double.IsNaN(cover.Height) || Math.Abs(cover.Height - targetHeight) > 0.5)
            cover.Height = targetHeight;

        cover.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, width, targetHeight)
        };
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
            flyout.Items.Add(CreateMenuItem("Open install location", game, OpenInstallLocationMenuItem_Click));
            flyout.Items.Add(CreateMenuItem("Change default executable", game, ChangeDefaultExecutableMenuItem_Click));
            flyout.Items.Add(CreateMenuItem("Patch with Goldberg", game, PatchWithGoldbergButton_Click));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(CreateMenuItem("Uninstall", game, UninstallMenuItem_Click));
        }

        flyout.ShowAt(sender as FrameworkElement, e.GetPosition(sender as UIElement));
        e.Handled = true;
    }

    private static MenuFlyoutItem CreateMenuItem(string text, GameEntry game, RoutedEventHandler click)
    {
        var item = new MenuFlyoutItem { Text = text, Tag = game };
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
                ? $"Remove {libraryOnly[0].Name} from the library?\nThis does not delete any game files and will only remove the game card."
                : $"Remove {libraryOnly.Count} library-only cards?\nThis does not delete any game files.";

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
                await _gameLibrary.RemoveAsync(game.AppId);
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

        try
        {
            // Start the game directly (no cmd). ShellExecute returns immediately for GUI apps.
            await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = true
                };
                Process.Start(psi);
            });
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"Unable to start game '{game.Name}' exe={exePath}");
            await _messageBoxService.ShowAsync(
                "Unable to start",
                $"Could not launch:\n{exePath}\n\n{ex.Message}");
        }
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

    private async void PatchWithGoldbergButton_Click(object sender, RoutedEventArgs e)
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
            await _goldbergPatchService.EnsureGoldbergAsync();

            IReadOnlyList<string> folders = _goldbergPatchService.FindSteamApiFolders(gameFolder);
            if (folders.Count == 0)
            {
                await _messageBoxService.ShowAsync(
                    "No Steam API found",
                    $"No steam_api.dll or steam_api64.dll was found under:\n{gameFolder}");
                return;
            }

            var checks = new List<CheckBox>(folders.Count);
            var list = new StackPanel { Spacing = 8 };
            list.Children.Add(new TextBlock
            {
                Text = "Select folders to patch:",
                TextWrapping = TextWrapping.WrapWholeWords,
                Margin = new Thickness(0, 0, 0, 4)
            });

            foreach (string folder in folders)
            {
                string relative = Path.GetRelativePath(gameFolder, folder);
                if (relative == ".")
                    relative = "(game root)";

                var check = new CheckBox
                {
                    Content = relative,
                    Tag = folder,
                    IsChecked = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                checks.Add(check);
                list.Children.Add(check);
            }

            var scroller = new ScrollViewer
            {
                Content = list,
                MaxHeight = 360,
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var result = await _messageBoxService.ShowAsync(
                $"Patch {game.Name}",
                scroller,
                "Patch",
                "Cancel");

            if (result != ContentDialogResult.Primary)
                return;

            var selected = checks
                .Where(box => box.IsChecked == true && box.Tag is string)
                .Select(box => (string)box.Tag!)
                .ToList();

            if (selected.Count == 0)
            {
                await _messageBoxService.ShowAsync("Nothing selected", "Select at least one folder to patch.");
                return;
            }

            await _goldbergPatchService.PatchFoldersAsync(selected, game.AppId);
            await _messageBoxService.ShowAsync(
                "Patch complete",
                $"Goldberg was applied to {selected.Count} location(s).\nOriginal DLLs were renamed to .bak.\nsteam_appid.txt was written with AppID {game.AppId}.");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"Goldberg patch failed for '{game.Name}' appId={game.AppId}");
            await _messageBoxService.ShowAsync("Patch failed", ex.Message);
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
                $"Remove {game.Name} from the library ?\nThis does not delete any game files and will only remove the game card.",
                "Remove",
                "Cancel");

            if (removeResult != ContentDialogResult.Primary)
                return;

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
    /// Usual uninstall flow for an installed title. Cancelling either prompt leaves the card as-is.
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

        var libraryResult = await _messageBoxService.ShowAsync(
            "Remove from library ?",
            $"Also remove the {game.Name} card from your library ?\n\n" +
            "Yes — delete the card.\n" +
            "No — keep it so you can Install again later.",
            "Yes, remove",
            "No, keep");

        bool removeFromLibrary = libraryResult == ContentDialogResult.Primary;

        try
        {
            await _uninstallService.UninstallAsync(game, removeFromLibrary);

            if (!removeFromLibrary)
            {
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
