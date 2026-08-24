using System.Collections.ObjectModel;
using System.Diagnostics;
using EZManifest.Models;
using EZManifest.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EZManifest.Views.Pages;

public sealed partial class LibraryPage : Page
{
    private readonly GameLibraryService _gameLibrary;
    private readonly GameUninstallService _uninstallService;
    private readonly AppMessageBoxService _messageBoxService;
    private readonly GameInstallPathService _installPathService;
    private readonly GoldbergPatchService _goldbergPatchService;
    private readonly WindowProvider _windowProvider;

    private bool _hasLoaded;
    private bool _refreshPending;
    private int _loadVersion;
    private Task? _loadTask;
    private string _searchQuery = string.Empty;

    public ObservableCollection<GameEntry> AppsList { get; } = new();
    public ObservableCollection<GameEntry> FilteredApps { get; } = new();

    public LibraryPage(
        GameLibraryService gameLibrary,
        GameUninstallService uninstallService,
        AppMessageBoxService messageBoxService,
        GameInstallPathService installPathService,
        GoldbergPatchService goldbergPatchService,
        WindowProvider windowProvider)
    {
        _gameLibrary = gameLibrary;
        _uninstallService = uninstallService;
        _messageBoxService = messageBoxService;
        _installPathService = installPathService;
        _goldbergPatchService = goldbergPatchService;
        _windowProvider = windowProvider;

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
        string.Equals(left.InstallPath, right.InstallPath, StringComparison.Ordinal);

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetGameEntry(sender) is not GameEntry game)
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
        string workingDirectory = Path.GetDirectoryName(exePath) ?? AppPaths.ExeDirectory;

        if (!string.IsNullOrWhiteSpace(gameFolder))
        {
            string fullGameFolder = Path.GetFullPath(gameFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string exeDir = Path.GetDirectoryName(exePath) ?? string.Empty;
            if (exePath.StartsWith(fullGameFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(exeDir, fullGameFolder, StringComparison.OrdinalIgnoreCase))
            {
                workingDirectory = fullGameFolder;
            }
        }

        workingDirectory = workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

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
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(_windowProvider.Window.AppWindow.Id)
        {
            ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List,
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.Unspecified,
            SettingsIdentifier = $"play-exe-{game.AppId}",
            CommitButtonText = "Select"
        };
        picker.FileTypeFilter.Add(".exe");

        if (!string.IsNullOrWhiteSpace(gameFolder))
        {
            bool folderOk = await Task.Run(() => Directory.Exists(gameFolder));
            if (folderOk)
            {
                picker.SuggestedStartFolder = gameFolder;
                picker.SuggestedFolder = gameFolder;
            }
        }

        var result = await picker.PickSingleFileAsync();
        return result?.Path;
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
            await _messageBoxService.ShowAsync("Patch failed", ex.Message);
        }
    }

    private async void UninstallMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetGameEntry(sender) is not GameEntry game)
            return;

        var result = await _messageBoxService.ShowAsync(
            "Uninstall game?",
            $"Are you sure you want to uninstall {game.Name}? This will delete its installed game files.",
            "Uninstall",
            "Cancel");

        if (result != ContentDialogResult.Primary)
            return;

        try
        {
            await _uninstallService.UninstallAsync(game);
            _refreshPending = true;
            await EnsureLoadedAsync(force: true);
        }
        catch (Exception ex)
        {
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
