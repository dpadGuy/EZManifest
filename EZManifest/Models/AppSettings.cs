namespace EZManifest.Models;

public sealed class AppSettings
{
    public string DownloadPath { get; set; } = string.Empty;

    /// <summary>Steam content cell used for CDN server discovery. 0 = Auto.</summary>
    public int CdnCellId { get; set; }
}
