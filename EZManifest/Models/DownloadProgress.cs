namespace EZManifest.Models;

public readonly record struct DownloadProgress(
    long DownloadedBytes,
    long TotalBytes,
    long NetworkBytesReceived = 0)
{
    public double Percentage =>
        TotalBytes <= 0 ? 0 : Math.Clamp(DownloadedBytes * 100d / TotalBytes, 0, 100);
}
