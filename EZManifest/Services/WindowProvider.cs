using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace EZManifest.Services;

public sealed class WindowProvider
{
    private Window? _window;

    public void SetWindow(Window window) => _window = window;

    public Window Window =>
        _window ?? throw new InvalidOperationException("Main window has not been registered.");

    public nint GetWindowHandle() => WindowNative.GetWindowHandle(Window);
}
