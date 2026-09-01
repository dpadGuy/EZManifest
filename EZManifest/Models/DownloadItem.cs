using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using Microsoft.UI.Xaml.Media;

namespace EZManifest.Models;

public class DownloadItem : INotifyPropertyChanged
{
    private string _gameName = string.Empty;
    public string GameName
    {
        get => _gameName;
        set { _gameName = value; OnPropertyChanged(nameof(GameName)); }
    }

    private string _appId = string.Empty;
    public string AppId
    {
        get => _appId;
        set { _appId = value; OnPropertyChanged(nameof(AppId)); }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(nameof(Status)); }
    }

    private ImageSource? _iconSource;
    public ImageSource? IconSource
    {
        get => _iconSource;
        set { _iconSource = value; OnPropertyChanged(nameof(IconSource)); }
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set
        {
            _progressValue = value;
            OnPropertyChanged(nameof(ProgressValue));
            OnPropertyChanged(nameof(ProgressPercentText));
            OnPropertyChanged(nameof(SizeProgressText));
        }
    }

    // Don't round 99.7% up to "100%" while bytes are still outstanding.
    public string ProgressPercentText =>
        TotalBytes > 0 && DownloadedBytes < TotalBytes
            ? $"{Math.Min(99, (int)ProgressValue)}%"
            : $"{ProgressValue:0}%";

    private long _downloadedBytes;
    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            _downloadedBytes = value;
            OnPropertyChanged(nameof(DownloadedBytes));
            OnPropertyChanged(nameof(SizeProgressText));
            OnPropertyChanged(nameof(ProgressPercentText));
        }
    }

    private long _totalBytes;
    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            _totalBytes = value;
            OnPropertyChanged(nameof(TotalBytes));
            OnPropertyChanged(nameof(SizeProgressText));
            OnPropertyChanged(nameof(ProgressPercentText));
        }
    }

    // CDN payload bytes only — used for speed, not disk write size.
    private long _networkBytesReceived;
    public long NetworkBytesReceived
    {
        get => _networkBytesReceived;
        set
        {
            UpdateDownloadSpeed(value);
            _networkBytesReceived = value;
            OnPropertyChanged(nameof(NetworkBytesReceived));
            OnPropertyChanged(nameof(SizeProgressText));
        }
    }

    private double _bytesPerSecond;
    private long _speedSampleBytes;
    private long _speedSampleTimestamp;

    public string SizeProgressText
    {
        get
        {
            if (TotalBytes <= 0)
                return string.Empty;

            string sizes = $"{FormatBytes(DownloadedBytes)} / {FormatBytes(TotalBytes)}";
            string speed = FormatSpeed(_bytesPerSecond);
            return string.IsNullOrEmpty(speed)
                ? sizes
                : $"{sizes}  ·  {speed}";
        }
    }

    private string _pauseButtonText = "Pause";
    public string PauseButtonText
    {
        get => _pauseButtonText;
        set { _pauseButtonText = value; OnPropertyChanged(nameof(PauseButtonText)); }
    }

    private readonly Stopwatch _elapsed = new();

    public string ElapsedText
    {
        get
        {
            TimeSpan elapsed = _elapsed.Elapsed;
            return elapsed.TotalHours >= 1
                ? $"Elapsed: {elapsed.Hours}h {elapsed.Minutes:00}m {elapsed.Seconds:00}s"
                : $"Elapsed: {elapsed.Minutes}m {elapsed.Seconds:00}s";
        }
    }

    public void StartElapsed() => _elapsed.Start();

    public void PauseElapsed() => _elapsed.Stop();

    public void ResumeElapsed() => _elapsed.Start();

    public void RefreshElapsed() => OnPropertyChanged(nameof(ElapsedText));

    public ICommand? CancelCommand { get; set; }
    public ICommand? PauseCommand { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void UpdateDownloadSpeed(long networkBytesReceived)
    {
        long now = Stopwatch.GetTimestamp();
        if (_speedSampleTimestamp == 0)
        {
            _speedSampleTimestamp = now;
            _speedSampleBytes = networkBytesReceived;
            return;
        }

        double elapsedSeconds = (now - _speedSampleTimestamp) / (double)Stopwatch.Frequency;
        if (elapsedSeconds < 0.35)
            return;

        long deltaBytes = networkBytesReceived - _speedSampleBytes;
        if (deltaBytes < 0)
            deltaBytes = 0;

        double instant = deltaBytes / elapsedSeconds;
        // Drop toward 0 quickly when heartbeats arrive with no new network bytes (stalled CDN).
        _bytesPerSecond = deltaBytes == 0
            ? _bytesPerSecond * 0.25
            : _bytesPerSecond <= 0
                ? instant
                : (_bytesPerSecond * 0.65) + (instant * 0.35);

        _speedSampleTimestamp = now;
        _speedSampleBytes = networkBytesReceived;
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond < 32)
            return string.Empty;

        string[] units = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
        double value = bytesPerSecond;
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

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = Math.Max(0, bytes);
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
