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
                return true;
            }

            _resumeSignal.TrySetResult(true);
            _resumeSignal = null;
            return false;
        }
    }

    public Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        Task waitTask;
        lock (_sync)
            waitTask = _resumeSignal?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;

        return WaitIgnoringDisposedAsync(waitTask, cancellationToken);
    }

    private static async Task WaitIgnoringDisposedAsync(Task waitTask, CancellationToken cancellationToken)
    {
        try
        {
            await waitTask;
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
