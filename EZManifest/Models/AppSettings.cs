namespace EZManifest.Models;

public sealed class AppSettings
{
    public const int DefaultMaxConcurrentChunks = 16;
    public const int GeForceNowDefaultMaxConcurrentChunks = 64;
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

    /// <summary>How the library list is ordered. See <see cref="LibrarySortMode"/>.</summary>
    public string LibrarySortMode { get; set; } = nameof(Models.LibrarySortMode.NameAsc);

    /// <summary>When true, library uses the Steam-style list instead of cover cards.</summary>
    public bool UseLibraryListView { get; set; } = true;

    /// <summary>When true, show a Windows notification after a game finishes installing.</summary>
    public bool NotifyOnInstallComplete { get; set; } = true;

    /// <summary>When true, check GitHub for a newer EZManifest installer on startup.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>When true, the Patch first-time how-to dialog has already been shown.</summary>
    public bool HasSeenPatchGuide { get; set; }

    /// <summary>When true, the DepotBox first-time how-to dialog has already been shown.</summary>
    public bool HasSeenDepotBoxGuide { get; set; }

    /// <summary>When true, the app welcome how-to dialog has already been shown.</summary>
    public bool HasSeenWelcomeGuide { get; set; }

    /// <summary>When true, Installs opens the user website instead of DepotBox.</summary>
    public bool UsePreferredManifestSource { get; set; }

    /// <summary>Custom manifest website used when UsePreferredManifestSource is true.</summary>
    public string PreferredManifestSourceUrl { get; set; } = string.Empty;

    /// <summary>When true, the depot picker lists every depot ID and skips Windows auto-select.</summary>
    public bool ShowAllDepotIds { get; set; }
}
