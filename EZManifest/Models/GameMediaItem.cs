using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace EZManifest.Models;

public sealed class GameMediaItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public string ThumbnailUrl { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string? VideoUrl { get; set; }
    public bool IsVideo { get; set; }

    public Uri? ThumbnailUri { get; set; }
    public Uri? ImageUri { get; set; }
    public Uri? VideoUri { get; set; }

    /// <summary>
    /// Playback candidates in preference order (progressive MP4, then H.264 HLS/DASH).
    /// Steam dropped many .mp4 files; later entries are adaptive manifests.
    /// </summary>
    public IReadOnlyList<Uri> VideoUris { get; set; } = [];

    public Visibility VideoBadgeVisibility =>
        IsVideo ? Visibility.Visible : Visibility.Collapsed;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedVisibility));
        }
    }

    public Visibility SelectedVisibility =>
        IsSelected ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class SteamStorePageInfo
{
    public string? AboutTheGame { get; set; }
    public IReadOnlyList<GameMediaItem> Media { get; set; } = [];
}
