using EZManifest.Models;
using EZManifest.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace EZManifest.Views.Pages;

public sealed partial class PatchPage : Page
{
    public const string HomeUrl = "https://gamecopyworld.eu/games/index.php";

    private readonly AppMessageBoxService _messageBoxService;
    private readonly AppSettingsService _settingsService;
    private readonly EasyListBlocker _easyList;
    private readonly PatchApplyService _patchApply;
    private readonly GameLibraryService _gameLibrary;
    private readonly FileExplorerPickerService _filePicker;
    private bool _ready;
    private int _patchBusy;

    public PatchPage(
        AppMessageBoxService messageBoxService,
        AppSettingsService settingsService,
        EasyListBlocker easyList,
        PatchApplyService patchApply,
        GameLibraryService gameLibrary,
        FileExplorerPickerService filePicker)
    {
        _messageBoxService = messageBoxService;
        _settingsService = settingsService;
        _easyList = easyList;
        _patchApply = patchApply;
        _gameLibrary = gameLibrary;
        _filePicker = filePicker;
        InitializeComponent();
        Loaded += PatchPage_Loaded;
        Unloaded += PatchPage_Unloaded;
        AddressBox.Text = HomeUrl;
    }

    private void PatchPage_Unloaded(object sender, RoutedEventArgs e) =>
        ReleaseBrowser();

    public void ReleaseBrowser()
    {
        if (!_ready && Browser.CoreWebView2 is null)
            return;

        try
        {
            Browser.CoreWebView2?.Stop();
            Browser.Close();
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Patch] Browser close: {ex.Message}");
        }

        BrowserHost.Children.Clear();
        Browser = new WebView2();
        BrowserHost.Children.Add(Browser);
        _ready = false;
        AddressBox.Text = HomeUrl;
        AppLog.Write("[Patch] WebView released");
    }

    private async void PatchPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            UpdateChrome();
            await ShowPatchGuideIfNeededAsync();
            return;
        }

        try
        {
            try
            {
                await _easyList.EnsureLoadedAsync();
            }
            catch (Exception ex)
            {
                AppLog.Write(ex, "EasyList failed to load");
                await _messageBoxService.ShowAsync(
                    "EasyList unavailable",
                    "Could not download or read EasyList. Ads will not be blocked until the list loads.\n\n" + ex.Message);
            }

            string userData = Path.Combine(AppPaths.DataDirectory, "WebView2");
            Directory.CreateDirectory(userData);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userData);
            await Browser.EnsureCoreWebView2Async();

            AttachAdBlocking(Browser.CoreWebView2);
            AttachPatchDownloads(Browser.CoreWebView2);

            Browser.NavigationStarting += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Uri))
                    AddressBox.Text = args.Uri;
            };
            Browser.NavigationCompleted += async (_, args) =>
            {
                UpdateChrome();
                if (args.IsSuccess)
                    await InjectEasyListHideAsync();
            };
            Browser.CoreWebView2.HistoryChanged += (_, _) => UpdateChrome();
            Browser.CoreWebView2.SourceChanged += (_, _) =>
            {
                string source = Browser.CoreWebView2.Source;
                if (!string.IsNullOrWhiteSpace(source))
                    AddressBox.Text = source;
            };

            Browser.Source = new Uri(HomeUrl);
            _ready = true;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Patch browser failed to start");
            AddressBox.Text = HomeUrl;
            await _messageBoxService.ShowAsync(
                "Browser unavailable",
                "Install the Microsoft Edge WebView2 Runtime to use Patch.\n\n" + ex.Message);
        }

        UpdateChrome();
        await ShowPatchGuideIfNeededAsync();
    }

    private async Task ShowPatchGuideIfNeededAsync()
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            if (settings.HasSeenPatchGuide)
                return;

            await ShowPatchGuideAsync();
            await _settingsService.UpdateAsync(s => s.HasSeenPatchGuide = true);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Patch first-time guide failed");
        }
    }

    private async Task ShowPatchGuideAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "How to apply game fixes to games with EZManifest",
            Content = new TextBlock
            {
                Text =
                    "!! Use this section in case a game doesn't start due to it's special DRM !!\n\n" +
                    "1. Search for your game\n" +
                    "2. Choose the right fix for your game (usually the latest one)\n" +
                    "3. Once download of the fix completes, choose the game you want to apply the fix to from your library\n" +
                    "4. Enjoy!",
                TextWrapping = TextWrapping.NoWrap
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

    private void AttachAdBlocking(CoreWebView2 core)
    {
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsReputationCheckingRequired = false;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += Core_WebResourceRequested;
        core.NewWindowRequested += Core_NewWindowRequested;
    }

    private void AttachPatchDownloads(CoreWebView2 core)
    {
        Directory.CreateDirectory(PatchApplyService.DownloadsFolder);
        core.DownloadStarting += Core_DownloadStarting;
    }

    private void Core_DownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
    {
        string suggested = NormalizeArchiveFileName(Path.GetFileName(args.ResultFilePath));
        string uri = args.DownloadOperation.Uri ?? string.Empty;
        if (!IsPatchArchive(suggested) && !IsPatchArchive(uri))
            return;

        string folder = PatchApplyService.DownloadsFolder;
        Directory.CreateDirectory(folder);

        if (string.IsNullOrWhiteSpace(suggested))
            suggested = "patch.zip";

        string dest = Path.Combine(folder, suggested);
        if (File.Exists(dest))
        {
            dest = Path.Combine(
                folder,
                $"{Path.GetFileNameWithoutExtension(suggested)}_{DateTime.UtcNow:yyyyMMddHHmmssfff}{Path.GetExtension(suggested)}");
        }

        args.Handled = true;
        args.ResultFilePath = dest;
        CoreWebView2DownloadOperation download = args.DownloadOperation;
        DispatcherQueue.TryEnqueue(() => _ = TrackPatchDownloadAsync(download, dest));
        AppLog.Write($"[Patch] Intercepted download → {dest}");
    }

    private async Task TrackPatchDownloadAsync(CoreWebView2DownloadOperation download, string path)
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
            Title = "Downloading game fix",
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
                          IsUsablePatchFile(path, sender.InterruptReason);
                if (!ok)
                {
                    AppLog.Write(
                        $"[Patch] Download {sender.State} interrupt={sender.InterruptReason} file={path}");
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
                AppLog.Write(ex, "Patch download cancel failed");
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
                $"The game fix download did not finish.\n{fileName}");
            await DiscardPatchDownloadAsync(path, sweepFolder: Volatile.Read(ref _patchBusy) == 0);
            return;
        }

        PatchApplyService.UnblockFile(path);
        await ApplyDownloadedPatchAsync(path);
    }

    private static bool IsUsablePatchFile(string path, CoreWebView2DownloadInterruptReason reason)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0 || !IsPatchArchive(path))
            return false;

        return reason is CoreWebView2DownloadInterruptReason.None
            or CoreWebView2DownloadInterruptReason.FileMalicious
            or CoreWebView2DownloadInterruptReason.FileSecurityCheckFailed
            or CoreWebView2DownloadInterruptReason.FileBlockedByPolicy;
    }

    private void Core_WebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        string? uri = args.Request?.Uri;
        if (string.IsNullOrWhiteSpace(uri))
            return;

        if (IsGcwAdMedia(uri) || _easyList.ShouldBlock(uri, args.ResourceContext, CurrentPageHost()))
            args.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
    }

    private void Core_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        string uri = args.Uri ?? string.Empty;
        if (string.IsNullOrWhiteSpace(uri) ||
            IsGcwAdMedia(uri) ||
            _easyList.ShouldBlock(uri, CoreWebView2WebResourceContext.Document, CurrentPageHost()))
        {
            return;
        }

        if (Uri.TryCreate(uri, UriKind.Absolute, out Uri? target) &&
            (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp))
        {
            sender.Navigate(uri);
        }
    }

    private async Task InjectEasyListHideAsync()
    {
        string script = _easyList.GetHideScript(CurrentPageHost()) +
            "(function(){var s=document.getElementById('ez-gcw-ii');if(!s){s=document.createElement('style');s.id='ez-gcw-ii';" +
            "s.textContent='img[src*=\"/ii/tc\"],video[src*=\"/ii/tc\"],source[src*=\"/ii/tc\"]," +
            "iframe[src*=\"/ii/tc\"],iframe[src*=\"a3.gamecopyworld\"]{display:none!important}';" +
            "(document.documentElement||document.head).appendChild(s);}})();";

        try
        {
            await Browser.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            AppLog.Write($"[EasyList] Hide script failed: {ex.Message}");
        }
    }

    private static bool IsGcwAdMedia(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed))
            return false;

        string host = parsed.Host;
        string path = parsed.AbsolutePath;
        if (!host.Contains("gamecopyworld.", StringComparison.OrdinalIgnoreCase))
            return false;

        return host.StartsWith("a3.", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/ii/tc", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/i/tc", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("_tc.php", StringComparison.OrdinalIgnoreCase);
    }

    private string? CurrentPageHost()
    {
        try
        {
            string? source = Browser.CoreWebView2?.Source;
            if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
                return uri.Host;
        }
        catch
        {
        }

        return Browser.Source?.Host;
    }

    private static async Task DiscardPatchDownloadAsync(string? archivePath, bool sweepFolder = true)
    {
        await PatchApplyService.TryDeleteFileAsync(archivePath);
        if (sweepFolder)
            await PatchApplyService.CleanupDownloadsAsync();
    }

    private async Task ApplyDownloadedPatchAsync(string archivePath)
    {
        if (!IsPatchArchive(archivePath))
        {
            await DiscardPatchDownloadAsync(archivePath, sweepFolder: Volatile.Read(ref _patchBusy) == 0);
            return;
        }

        if (Interlocked.CompareExchange(ref _patchBusy, 1, 0) != 0)
        {
            await _messageBoxService.ShowAsync(
                "Game fix already running",
                "Wait for the current game fix to finish, then download again.");
            await DiscardPatchDownloadAsync(archivePath, sweepFolder: false);
            return;
        }

        string? extractDir = null;
        try
        {
            var games = (await _gameLibrary.LoadAsync())
                .Where(g => g.IsInstalled &&
                            !string.IsNullOrWhiteSpace(g.InstallPath) &&
                            Directory.Exists(g.InstallPath))
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            string? gameFolder = await PromptPatchTargetAsync(archivePath, games);
            if (string.IsNullOrWhiteSpace(gameFolder))
                return;

            var confirm = await _messageBoxService.ShowAsync(
                "Overwrite game files?",
                $"Extract the archive, take the inner files folder, and copy it over:\n{gameFolder}\n\nExisting files with the same names will be overwritten.",
                "Apply",
                "Cancel");
            if (confirm != ContentDialogResult.Primary)
                return;

            int copied = 0;
            await RunFixProgressAsync(async (progress, cancellationToken) =>
            {
                extractDir = await _patchApply.ExtractArchiveAsync(archivePath, progress, cancellationToken);
                string? filesFolder = PatchApplyService.FindFilesFolder(extractDir);
                if (filesFolder is null)
                {
                    throw new DirectoryNotFoundException(
                        "The archive extracted, but no files folder was found.\nExpected something like:\nrune-doom.the.dark.ages.7z\\files");
                }

                copied = await _patchApply.CopyFilesOverAsync(filesFolder, gameFolder, progress, cancellationToken);
            });

            AppLog.Write($"[Patch] Applied '{Path.GetFileName(archivePath)}' → {gameFolder} ({copied} file(s))");
            await _messageBoxService.ShowAsync(
                "Game fix applied",
                $"Copied {copied} file(s) from files into:\n{gameFolder}");
        }
        catch (OperationCanceledException)
        {
            AppLog.Write($"[Patch] Game fix cancelled for {archivePath}");
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"Game fix apply failed for {archivePath}");
            await _messageBoxService.ShowAsync("Game fix failed", ex.Message);
        }
        finally
        {
            PatchApplyService.TryDeleteDirectory(extractDir);
            await DiscardPatchDownloadAsync(archivePath);
            Interlocked.Exchange(ref _patchBusy, 0);
        }
    }

    private async Task RunFixProgressAsync(Func<IProgress<PatchApplyProgress>, CancellationToken, Task> work)
    {
        var statusText = new TextBlock
        {
            Text = "Extracting…",
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

        using var cts = new CancellationTokenSource();
        bool allowClose = false;
        var dialog = new ContentDialog
        {
            Title = "Applying game fix",
            Content = content,
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
        };
        dialog.Resources["ContentDialogMinHeight"] = 0.0;
        dialog.Resources["ContentDialogMinWidth"] = 420.0;
        dialog.Resources["ContentDialogMaxWidth"] = 560.0;
        dialog.Closing += (_, args) =>
        {
            if (allowClose)
                return;

            args.Cancel = true;
            cts.Cancel();
        };

        var progress = new Progress<PatchApplyProgress>(update =>
        {
            string phase = update.Phase.Equals("Applying", StringComparison.OrdinalIgnoreCase)
                ? "Copying files…"
                : "Extracting…";
            statusText.Text = phase;
            if (update.Total > 0)
            {
                progressBar.IsIndeterminate = false;
                progressBar.Value = Math.Clamp(update.Done * 100.0 / update.Total, 0, 100);
                detailText.Text = string.IsNullOrWhiteSpace(update.Current)
                    ? $"{update.Done} / {update.Total}"
                    : $"{update.Done} / {update.Total}  {update.Current}";
            }
            else
            {
                progressBar.IsIndeterminate = true;
                detailText.Text = string.IsNullOrWhiteSpace(update.Current) ? "Working…" : update.Current;
            }
        });

        var showTask = dialog.ShowAsync().AsTask();
        try
        {
            await work(progress, cts.Token);
        }
        finally
        {
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
    }

    private async Task<string?> PromptPatchTargetAsync(string archivePath, List<GameEntry> games)
    {
        string archiveName = Path.GetFileName(archivePath);
        if (games.Count == 0)
        {
            var browseOnly = await _messageBoxService.ShowAsync(
                "Apply game fix",
                $"Downloaded {archiveName}.\nNo installed library game was found. Browse to the game folder to apply the game fix?",
                "Browse",
                "Cancel");
            return browseOnly == ContentDialogResult.Primary ? await PickGameFolderAsync() : null;
        }

        var combo = new ComboBox
        {
            MinWidth = 360,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = games,
            DisplayMemberPath = nameof(GameEntry.Name),
            SelectedItem = GuessGame(archiveName, games) ?? games[0]
        };
        var search = new TextBox
        {
            MinWidth = 360,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Search games"
        };
        search.TextChanged += (_, _) =>
        {
            string query = search.Text?.Trim() ?? string.Empty;
            List<GameEntry> filtered = string.IsNullOrEmpty(query)
                ? games
                : games.Where(g =>
                    g.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    g.AppId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

            GameEntry? selected = combo.SelectedItem as GameEntry;
            combo.ItemsSource = filtered;
            if (selected is not null && filtered.Contains(selected))
                combo.SelectedItem = selected;
            else
                combo.SelectedItem = filtered.Count > 0 ? filtered[0] : null;
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Downloaded {archiveName}.\nChoose the game to overwrite with the archive's files folder.",
            TextWrapping = TextWrapping.WrapWholeWords
        });
        panel.Children.Add(search);
        panel.Children.Add(combo);

        var result = await _messageBoxService.ShowAsync(
            "Apply game fix",
            panel,
            "Apply game fix",
            "Cancel",
            "Browse folder");

        if (result == ContentDialogResult.Primary && combo.SelectedItem is GameEntry game)
            return Path.GetFullPath(game.InstallPath);

        if (result == ContentDialogResult.Secondary)
            return await PickGameFolderAsync();

        return null;
    }

    private Task<string?> PickGameFolderAsync() =>
        _filePicker.PickFolderAsync("Select game folder", startAtThisPc: true);

    private static GameEntry? GuessGame(string archiveName, IReadOnlyList<GameEntry> games)
    {
        string needle = NormalizeName(Path.GetFileNameWithoutExtension(archiveName));
        if (needle.Length == 0)
            return null;

        GameEntry? best = null;
        int bestScore = 0;
        foreach (GameEntry game in games)
        {
            string name = NormalizeName(game.Name);
            int score = 0;
            if (name.Contains(needle, StringComparison.Ordinal) || needle.Contains(name, StringComparison.Ordinal))
                score = Math.Min(name.Length, needle.Length);
            else
            {
                foreach (string token in needle.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.Length >= 3 && name.Contains(token, StringComparison.Ordinal))
                        score += token.Length;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = game;
            }
        }

        return bestScore > 0 ? best : null;
    }

    private static string NormalizeName(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsPatchArchive(string path)
    {
        string name = NormalizeArchiveFileName(Path.GetFileName(path.TrimEnd('/')));
        if (string.IsNullOrWhiteSpace(name) && Uri.TryCreate(path, UriKind.Absolute, out Uri? uri))
            name = NormalizeArchiveFileName(Path.GetFileName(uri.AbsolutePath));

        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeArchiveFileName(string? name)
    {
        name = SanitizeFileName(name ?? string.Empty);
        if (name.EndsWith("!7z", StringComparison.OrdinalIgnoreCase))
            return name[..^3] + ".7z";
        if (name.EndsWith("!zip", StringComparison.OrdinalIgnoreCase))
            return name[..^4] + ".zip";
        if (name.EndsWith("!rar", StringComparison.OrdinalIgnoreCase))
            return name[..^4] + ".rar";
        return name;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoBack)
            Browser.GoBack();
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoForward)
            Browser.GoForward();
    }

    private void GoButton_Click(object sender, RoutedEventArgs e) => NavigateToAddress();

    private void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            NavigateToAddress();
        }
    }

    private void NavigateToAddress()
    {
        if (!_ready)
            return;

        string text = AddressBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }

        Browser.Source = uri;
    }

    private void UpdateChrome()
    {
        BackButton.IsEnabled = Browser.CanGoBack;
        ForwardButton.IsEnabled = Browser.CanGoForward;
        if (Browser.Source is not null)
            AddressBox.Text = Browser.Source.ToString();
    }
}
