using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;

namespace EZManifest.Models;

public sealed class GameEntry : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _image = string.Empty;
    private bool? _hasCoverArt;

    public string AppId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public string Image
    {
        get => _image;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(_image, next, StringComparison.Ordinal))
                return;

            _image = next;
            _hasCoverArt = null;
        }
    }

    public string StartLocation { get; set; } = string.Empty;
    /// <summary>Install folder for this game (where files are downloaded).</summary>
    public string InstallPath { get; set; } = string.Empty;
    /// <summary>True after a download finishes; false for library-only / pending install.</summary>
    public bool IsInstalled { get; set; }

    /// <summary>Play when installed; Install when the title is library-only.</summary>
    [JsonIgnore]
    public string PrimaryActionText => IsInstalled ? "Play" : "Install";

    [JsonIgnore]
    public Visibility PlayButtonVisibility =>
        IsInstalled ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility InstallButtonVisibility =>
        IsInstalled ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Larger type for short names; smaller so long titles still fit the card.</summary>
    [JsonIgnore]
    public double TitleFontSize
    {
        get
        {
            int length = Name.Length;
            if (length <= 18)
                return 16;
            if (length <= 28)
                return 14;
            if (length <= 40)
                return 12;
            return 11;
        }
    }

    [JsonIgnore]
    public bool HasCoverArt =>
        _hasCoverArt ??= !string.IsNullOrWhiteSpace(Image) && File.Exists(Image);

    [JsonIgnore]
    public Visibility CoverArtVisibility =>
        HasCoverArt ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility NoArtVisibility =>
        HasCoverArt ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>UI-only multi-select highlight (not persisted).</summary>
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionVisibility));
        }
    }

    [JsonIgnore]
    public Visibility SelectionVisibility =>
        IsSelected ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
