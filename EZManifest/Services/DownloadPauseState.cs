namespace EZManifest.Services;

public sealed class DownloadPauseState
{
    private readonly object _sync = new();
    private TaskCompletionSource<bool>? _resumeSignal;

    public bool Toggle()
    {
        lock (_sync)
        {
            if (_resumeSignal is null)
            {
                _resumeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                AppLog.Write("[Downloads] Pause latch set");
                return true;
            }

            _resumeSignal.TrySetResult(true);
            _resumeSignal = null;
            AppLog.Write("[Downloads] Pause latch cleared (resume)");
            return false;
        }
    }

    public Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        Task waitTask;
        bool paused;
        lock (_sync)
        {
            paused = _resumeSignal is not null;
            waitTask = _resumeSignal?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
        }

        if (paused)
            AppLog.Write("[Downloads] Worker waiting while paused...");

        return WaitIgnoringDisposedAsync(waitTask, paused, cancellationToken);
    }

    private static async Task WaitIgnoringDisposedAsync(
        Task waitTask,
        bool wasPaused,
        CancellationToken cancellationToken)
    {
        try
        {
            await waitTask;
            if (wasPaused)
                AppLog.Write("[Downloads] Worker resumed after pause");
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            AppLog.Write("[Downloads] Pause wait aborted (cancelled)");
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
