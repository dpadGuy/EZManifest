namespace EZManifest.Services;

/// <summary>Navigates the main window without creating a DI cycle with pages.</summary>
public sealed class AppNavigationService
{
    private Action<string>? _navigate;

    public void Register(Action<string> navigate) => _navigate = navigate;

    public void Navigate(string tag) => _navigate?.Invoke(tag);
}
