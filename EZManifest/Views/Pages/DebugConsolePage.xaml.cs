using System.Text;
using EZManifest.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace EZManifest.Views.Pages;

public sealed partial class DebugConsolePage : Page
{
    private const int UiCap = 2000;

    private readonly DebugLogService _logService;
    private readonly List<string> _lines = new();
    private readonly List<string> _pending = new();
    private bool _listening;
    private bool _flushQueued;
    private bool _uiDirty;

    public DebugConsolePage(DebugLogService logService)
    {
        _logService = logService;
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AttachAndRefresh();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => AttachAndRefresh();

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private bool CanTouchUi =>
        _listening &&
        IsLoaded &&
        LogTextBlock is not null &&
        LogScrollViewer is not null &&
        AutoScrollToggle is not null;

    private void AttachAndRefresh()
    {
        if (!_listening)
        {
            _logService.LineReceived += OnLogLine;
            _listening = true;
        }

        var snapshot = _logService.GetSnapshot();
        _lines.Clear();
        int start = Math.Max(0, snapshot.Count - UiCap);
        for (int i = start; i < snapshot.Count; i++)
            _lines.Add(snapshot[i]);

        lock (_pending)
            _pending.Clear();
        _uiDirty = false;

        if (!CanTouchUi)
            return;

        try
        {
            double width = LogScrollViewer.ViewportWidth;
            if (width > 0)
                LogTextBlock.Width = width;

            ApplyText(scrollToEnd: AutoScrollToggle.IsOn);
        }
        catch
        {
            // Page may be tearing down during app close.
        }
    }

    private void Detach()
    {
        if (_listening)
        {
            _logService.LineReceived -= OnLogLine;
            _listening = false;
        }

        lock (_pending)
            _pending.Clear();
        _flushQueued = false;
        _uiDirty = false;
    }

    private void OnLogLine(string line)
    {
        if (!_listening)
            return;

        lock (_pending)
            _pending.Add(line);

        if (_flushQueued)
            return;

        _flushQueued = true;
        DispatcherQueue.TryEnqueue(FlushPending);
    }

    private void FlushPending()
    {
        _flushQueued = false;

        List<string> batch;
        lock (_pending)
        {
            if (_pending.Count == 0)
                return;
            batch = _pending.ToList();
            _pending.Clear();
        }

        _lines.AddRange(batch);
        if (_lines.Count > UiCap)
            _lines.RemoveRange(0, _lines.Count - UiCap);

        // Queued callback can still run after Unloaded / app close.
        if (!CanTouchUi)
            return;

        try
        {
            bool rebuilt = batch.Count > 0 && _lines.Count == UiCap;
            bool selecting = !string.IsNullOrEmpty(LogTextBlock.SelectedText);
            if (selecting)
            {
                _uiDirty = true;
                return;
            }

            if (rebuilt || _uiDirty)
            {
                _uiDirty = false;
                ApplyText(scrollToEnd: AutoScrollToggle.IsOn);
                return;
            }

            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(LogTextBlock.Text))
                sb.AppendLine();
            sb.AppendJoin(Environment.NewLine, batch);
            LogTextBlock.Text += sb.ToString();

            if (AutoScrollToggle.IsOn)
                ScrollToEnd();
        }
        catch
        {
            // Ignore UI failures during shutdown / navigation teardown.
        }
    }

    private void ApplyText(bool scrollToEnd)
    {
        if (!CanTouchUi)
            return;

        LogTextBlock.Text = string.Join(Environment.NewLine, _lines);
        if (scrollToEnd)
            ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (!CanTouchUi)
            return;

        try
        {
            LogScrollViewer.UpdateLayout();
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, disableAnimation: true);
        }
        catch
        {
        }
    }

    private async void CopyAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanTouchUi)
            return;

        string text = LogTextBlock.Text;
        if (string.IsNullOrEmpty(text))
            return;

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);

        if (CopyAllButton is null)
            return;

        CopyAllButton.Content = "Copied";
        await Task.Delay(1200);
        if (CopyAllButton is not null && IsLoaded)
            CopyAllButton.Content = "Copy all";
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _logService.Clear();
        _lines.Clear();
        lock (_pending)
            _pending.Clear();
        _uiDirty = false;

        if (CanTouchUi)
            LogTextBlock.Text = string.Empty;
    }

    private void LogScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!CanTouchUi)
            return;

        // Constrain width so TextWrapping actually wraps inside the ScrollViewer.
        double width = LogScrollViewer.ViewportWidth;
        if (width > 0)
            LogTextBlock.Width = width;
    }
}
