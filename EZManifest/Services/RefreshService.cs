namespace EZManifest.Services;

public static class RefreshService
{
    public static event Action? OnListRefreshRequested;

    public static void RequestRefresh()
    {
        OnListRefreshRequested?.Invoke();
    }
}
