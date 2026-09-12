using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace EZManifest;

public static class Program
{
    public const string ToastActivateArgument = "--toast-activate";
    internal const string ShowWindowEventName = @"Local\EZManifest.ShowWindow";
    private const string SingleInstanceMutexName = @"Local\EZManifest.SingleInstance";
    private const int AllowSetForegroundAny = -1;

    private static Mutex? _singleInstanceMutex;

    [STAThread]
    private static void Main(string[] args)
    {
        bool createdNew;
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return;
        }

        ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }

    internal static void SignalExistingInstance()
    {
        try
        {
            AllowSetForegroundWindow(AllowSetForegroundAny);
            using var ev = EventWaitHandle.OpenExisting(ShowWindowEventName);
            ev.Set();
        }
        catch
        {
            // The running instance is not listening yet.
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
