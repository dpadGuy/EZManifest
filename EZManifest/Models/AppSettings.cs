namespace EZManifest.Models;

public sealed class AppSettings
{
    public const int DefaultMaxConcurrentChunks = 16;
    public const int MinConcurrentChunks = 1;
    public const int MaxConcurrentChunksLimit = 64;

    public string DownloadPath { get; set; } = string.Empty;

    /// <summary>Steam content cell used for CDN server discovery. 0 = Auto.</summary>
    public int CdnCellId { get; set; }

    /// <summary>How many depot chunks to download in parallel.</summary>
    public int MaxConcurrentChunks { get; set; } = DefaultMaxConcurrentChunks;

    /// <summary>UI theme: "Light", "Dark", or empty to follow the system.</summary>
    public string Theme { get; set; } = string.Empty;

    /// <summary>When true, library shows only installed/downloaded games.</summary>
    public bool ShowDownloadedOnly { get; set; }

    /// <summary>When true, library uses the Steam-style list instead of cover cards.</summary>
    public bool UseLibraryListView { get; set; } = true;

    /// <summary>When true, show a Windows notification after a game finishes installing.</summary>
    public bool NotifyOnInstallComplete { get; set; } = true;
}
