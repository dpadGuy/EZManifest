namespace EZManifest.Models;

public sealed class DepotDisplayInfo
{
    public string DepotId { get; init; } = string.Empty;
    public string ManifestId { get; init; } = string.Empty;
    public string Configuration { get; init; } = string.Empty;
    public string OsLabel { get; init; } = string.Empty;
    public long? SizeBytes { get; init; }
    public long? DownloadBytes { get; init; }
    public bool HasLocalManifest { get; init; }

    public string SizeText => FormatBytes(SizeBytes);
    public string DownloadText => FormatBytes(DownloadBytes);

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null or < 0)
            return "—";

        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
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
