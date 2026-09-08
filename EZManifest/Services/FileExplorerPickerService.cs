using EZManifest.Models;
using EZManifest.Views.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EZManifest.Services;

public sealed class FileExplorerPickerService
{
    private readonly WindowProvider _windowProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileExplorerPickerService(WindowProvider windowProvider) =>
        _windowProvider = windowProvider;

    public async Task<string?> PickFolderAsync(
        string title = "Select folder",
        string? initialPath = null,
        bool startAtThisPc = false)
    {
        IReadOnlyList<string> paths = await ShowAsync(new FileExplorerPickerOptions
        {
            Title = title,
            ConfirmText = "Select Folder",
            InitialPath = initialPath,
            StartAtThisPc = startAtThisPc,
            FoldersOnly = true,
            AllowMultiSelect = false
        });
        return paths.Count > 0 ? paths[0] : null;
    }

    public Task<IReadOnlyList<string>> PickFoldersAsync(
        string title = "Select folders",
        string? initialPath = null)
    {
        return ShowAsync(new FileExplorerPickerOptions
        {
            Title = title,
            ConfirmText = "Select Folder",
            InitialPath = initialPath,
            FoldersOnly = true,
            AllowMultiSelect = true
        });
    }

    public Task<IReadOnlyList<string>> PickFilesAsync(
        IReadOnlyList<string> extensions,
        string title = "Open",
        string? initialPath = null,
        bool allowMultiSelect = true)
    {
        return ShowAsync(new FileExplorerPickerOptions
        {
            Title = title,
            ConfirmText = "Open",
            InitialPath = initialPath,
            FoldersOnly = false,
            AllowMultiSelect = allowMultiSelect,
            FileExtensions = extensions
        });
    }

    private async Task<IReadOnlyList<string>> ShowAsync(FileExplorerPickerOptions options)
    {
        await _gate.WaitAsync();
        try
        {
            var picker = new FileExplorerPickerDialog();
            picker.Configure(options);

            var dialog = new ContentDialog
            {
                Title = options.Title,
                Content = picker,
                PrimaryButtonText = options.ConfirmText,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = ResolveXamlRoot(),
                RequestedTheme = ResolveTheme()
            };
            dialog.Resources["ContentDialogMinWidth"] = 760.0;
            dialog.Resources["ContentDialogMaxWidth"] = 1000.0;
            dialog.Resources["ContentDialogMinHeight"] = 560.0;
            dialog.Resources["ContentDialogMaxHeight"] = 760.0;

            bool accepted = false;
            picker.Confirmed += () =>
            {
                accepted = true;
                dialog.Hide();
            };

            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (!picker.TryCommit())
                    args.Cancel = true;
                else
                    accepted = true;
            };

            await dialog.ShowAsync();
            return accepted ? picker.SelectedPaths : Array.Empty<string>();
        }
        finally
        {
            _gate.Release();
        }
    }

    private XamlRoot ResolveXamlRoot()
    {
        if (_windowProvider.Window.Content is FrameworkElement root && root.XamlRoot is not null)
            return root.XamlRoot;

        throw new InvalidOperationException("Main window XamlRoot is not available.");
    }

    private ElementTheme ResolveTheme()
    {
        if (_windowProvider.Window.Content is FrameworkElement root)
            return root.ActualTheme;

        return ElementTheme.Default;
    }
}
