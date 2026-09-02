using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EZManifest.Services;

public sealed class AppMessageBoxService
{
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly WindowProvider _windowProvider;
    private XamlRoot? _xamlRoot;

    public AppMessageBoxService(WindowProvider windowProvider) =>
        _windowProvider = windowProvider;

    public void SetXamlRoot(XamlRoot xamlRoot) => _xamlRoot = xamlRoot;

    public async Task<ContentDialogResult> ShowAsync(
        string title,
        string content,
        string? primaryButtonText = null,
        string closeButtonText = "OK",
        string? secondaryButtonText = null)
    {
        return await ShowAsync(title, (object)content, primaryButtonText, closeButtonText, secondaryButtonText);
    }

    public async Task<ContentDialogResult> ShowAsync(
        string title,
        object content,
        string? primaryButtonText = null,
        string closeButtonText = "OK",
        string? secondaryButtonText = null)
    {
        await _dialogGate.WaitAsync();
        try
        {
            if (_xamlRoot is null)
                throw new InvalidOperationException("XamlRoot has not been set. Call SetXamlRoot from the main window first.");

            object dialogContent = content is string text
                ? new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    Margin = new Thickness(0, 0, 0, 4)
                }
                : content;

            var dialog = new ContentDialog
            {
                Title = title,
                Content = dialogContent,
                CloseButtonText = closeButtonText,
                XamlRoot = _xamlRoot,
                RequestedTheme = ResolveTheme()
            };

            // Short string notices should size to content; custom content keeps roomier defaults.
            dialog.Resources["ContentDialogMinHeight"] = 0.0;
            if (content is string)
            {
                dialog.Resources["ContentDialogMinWidth"] = 320.0;
                dialog.Resources["ContentDialogMaxWidth"] = string.IsNullOrWhiteSpace(secondaryButtonText)
                    ? 480.0
                    : 560.0;
            }

            if (!string.IsNullOrWhiteSpace(primaryButtonText))
                dialog.PrimaryButtonText = primaryButtonText;

            if (!string.IsNullOrWhiteSpace(secondaryButtonText))
                dialog.SecondaryButtonText = secondaryButtonText;

            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private ElementTheme ResolveTheme()
    {
        try
        {
            if (_windowProvider.Window.Content is FrameworkElement root)
                return root.ActualTheme;
        }
        catch
        {
        }

        return ElementTheme.Default;
    }
}
