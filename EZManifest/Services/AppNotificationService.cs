using Microsoft.UI.Xaml.Controls;

namespace EZManifest.Services;

public sealed class AppNotificationService
{
    private InfoBar? _infoBar;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private int _showVersion;

    public void Initialize(InfoBar infoBar, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        _infoBar = infoBar;
        _dispatcher = dispatcher;
    }

    public void Show(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        int version = Interlocked.Increment(ref _showVersion);

        void Apply()
        {
            if (_infoBar is null) return;
            _infoBar.Title = title;
            _infoBar.Message = message;
            _infoBar.Severity = severity;
            _infoBar.IsOpen = true;
        }

        if (_dispatcher is null || _dispatcher.HasThreadAccess)
            Apply();
        else
            _dispatcher.TryEnqueue(Apply);

        _ = DismissAfterDelayAsync(version);
    }

    private async Task DismissAfterDelayAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        void Close()
        {
            if (_infoBar is null || version != _showVersion)
                return;
            _infoBar.IsOpen = false;
        }

        if (_dispatcher is null)
            return;

        if (_dispatcher.HasThreadAccess)
            Close();
        else
            _dispatcher.TryEnqueue(Close);
    }
}
