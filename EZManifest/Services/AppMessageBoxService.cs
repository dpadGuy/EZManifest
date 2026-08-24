using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EZManifest.Services;

public sealed class AppMessageBoxService
{
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private XamlRoot? _xamlRoot;

    public void SetXamlRoot(XamlRoot xamlRoot) => _xamlRoot = xamlRoot;

    public async Task<ContentDialogResult> ShowAsync(
        string title,
        string content,
        string? primaryButtonText = null,
        string closeButtonText = "OK")
    {
        return await ShowAsync(title, (object)content, primaryButtonText, closeButtonText);
    }

    public async Task<ContentDialogResult> ShowAsync(
        string title,
        object content,
        string? primaryButtonText = null,
        string closeButtonText = "OK")
    {
        await _dialogGate.WaitAsync();
        try
        {
            if (_xamlRoot is null)
                throw new InvalidOperationException("XamlRoot has not been set. Call SetXamlRoot from the main window first.");

            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = closeButtonText,
                XamlRoot = _xamlRoot
            };

            if (!string.IsNullOrWhiteSpace(primaryButtonText))
                dialog.PrimaryButtonText = primaryButtonText;

            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogGate.Release();
        }
    }
}
