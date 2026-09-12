using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace EZManifest.Services;

public sealed class WindowProvider
{
    private const int SwRestore = 9;
    private const int SwMaximize = 3;
    private Window? _window;
    private EventWaitHandle? _showEvent;
    private Thread? _showListener;

    public void SetWindow(Window window)
    {
        _window = window;
        StartShowListener();
    }

    public Window Window =>
        _window ?? throw new InvalidOperationException("Main window has not been registered.");

    public nint GetWindowHandle() => WindowNative.GetWindowHandle(Window);

    public void ActivateExistingWindow(bool maximize = false)
    {
        if (_window is null)
            return;

        void Show()
        {
            nint hwnd = WindowNative.GetWindowHandle(_window);
            if (hwnd == 0)
                return;

            if (IsIconic(hwnd))
                ShowWindow(hwnd, SwRestore);

            if (maximize)
            {
                if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
                    presenter.Maximize();
                else
                    ShowWindow(hwnd, SwMaximize);
            }

            _window.Activate();
            SetForegroundWindow(hwnd);
            if (maximize)
                WindowsToastService.ClearInstallToast();
        }

        if (_window.DispatcherQueue.HasThreadAccess)
            Show();
        else
            _window.DispatcherQueue.TryEnqueue(Show);
    }

    private void StartShowListener()
    {
        if (_showListener is not null)
            return;

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowWindowEventName);
        _showListener = new Thread(() =>
        {
            while (true)
            {
                if (_showEvent.WaitOne())
                    ActivateExistingWindow(maximize: true);
            }
        })
        {
            IsBackground = true,
            Name = "EZManifest.ShowWindow"
        };
        _showListener.Start();
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);
}
