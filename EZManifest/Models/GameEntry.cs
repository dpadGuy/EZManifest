using System.Collections.ObjectModel;
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
    private bool? _hasIcon;

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
            _hasIcon = null;
        }
    }

    public string StartLocation { get; set; } = string.Empty;
    /// <summary>Install folder for this game (where files are downloaded).</summary>
    public string InstallPath { get; set; } = string.Empty;
    /// <summary>True after a download finishes; false for library-only / pending install.</summary>
    public bool IsInstalled { get; set; }

    private long? _installSizeBytes;
    /// <summary>Installed folder size, or Steam Windows depot size before install.</summary>
    public long? InstallSizeBytes
    {
        get => _installSizeBytes;
        set
        {
            if (_installSizeBytes == value)
                return;

            _installSizeBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InstallSizeText));
            OnPropertyChanged(nameof(InstallSizeVisibility));
        }
    }

    [JsonIgnore]
    public string InstallSizeText => FormatInstallSize(InstallSizeBytes);

    [JsonIgnore]
    public Visibility InstallSizeVisibility =>
        InstallSizeBytes is > 0 ? Visibility.Visible : Visibility.Collapsed;

    private string _aboutTheGame = string.Empty;

    /// <summary>Plain-text Steam store about section, cached after first fetch.</summary>
    public string AboutTheGame
    {
        get => _aboutTheGame;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(_aboutTheGame, next, StringComparison.Ordinal))
                return;

            _aboutTheGame = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AboutTheGameVisibility));
        }
    }

    [JsonIgnore]
    public bool AboutTheGameLoaded { get; set; }

    [JsonIgnore]
    public Visibility AboutTheGameVisibility =>
        string.IsNullOrWhiteSpace(AboutTheGame) ? Visibility.Collapsed : Visibility.Visible;

    [JsonIgnore]
    public ObservableCollection<GameMediaItem> MediaItems { get; } = new();

    [JsonIgnore]
    public bool MediaLoaded { get; set; }

    [JsonIgnore]
    public Visibility GameMediaVisibility =>
        MediaItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void SetMedia(IReadOnlyList<GameMediaItem> items)
    {
        MediaItems.Clear();
        foreach (GameMediaItem item in items)
            MediaItems.Add(item);

        MediaLoaded = true;
        OnPropertyChanged(nameof(GameMediaVisibility));
    }

    /// <summary>Play when installed; Install when the title is library-only.</summary>
    [JsonIgnore]
    public string PrimaryActionText => IsInstalled ? "Play" : "Install";

    private bool _isRunning;

    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
                return;

            _isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlayButtonVisibility));
            OnPropertyChanged(nameof(StopButtonVisibility));
        }
    }

    [JsonIgnore]
    public Visibility PlayButtonVisibility =>
        IsInstalled && !IsRunning ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility InstalledPlayIconVisibility =>
        IsInstalled ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility StopButtonVisibility =>
        IsRunning ? Visibility.Visible : Visibility.Collapsed;

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
    public string? ResolvedIconPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Image))
                return null;

            string? directory = Path.GetDirectoryName(Image);
            return string.IsNullOrWhiteSpace(directory)
                ? null
                : Path.Combine(directory, "GameIcon.jpg");
        }
    }

    [JsonIgnore]
    public bool HasIcon =>
        _hasIcon ??= !string.IsNullOrWhiteSpace(ResolvedIconPath) && File.Exists(ResolvedIconPath);

    [JsonIgnore]
    public bool HasListArt => HasIcon || HasCoverArt;

    [JsonIgnore]
    public Visibility CoverArtVisibility =>
        HasCoverArt ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility NoArtVisibility =>
        HasCoverArt ? Visibility.Collapsed : Visibility.Visible;

    [JsonIgnore]
    public Visibility ListArtVisibility =>
        HasListArt ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility NoListArtVisibility =>
        HasListArt ? Visibility.Collapsed : Visibility.Visible;

    public void RefreshArtworkFlags()
    {
        _hasCoverArt = null;
        _hasIcon = null;
        OnPropertyChanged(nameof(HasCoverArt));
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(nameof(HasListArt));
        OnPropertyChanged(nameof(CoverArtVisibility));
        OnPropertyChanged(nameof(NoArtVisibility));
        OnPropertyChanged(nameof(ListArtVisibility));
        OnPropertyChanged(nameof(NoListArtVisibility));
    }

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

    private static string FormatInstallSize(long? bytes)
    {
        if (bytes is null or <= 0)
            return string.Empty;

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes.Value;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:0} {units[unitIndex]}"
            : $"{value:0.##} {units[unitIndex]}";
    }
}
