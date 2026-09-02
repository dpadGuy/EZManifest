namespace EZManifest.Models;

public sealed class AppUpdateInfo
{
    public required Version Version { get; init; }
    public required string TagName { get; init; }
    public required Uri DownloadUri { get; init; }
    public required string FileName { get; init; }
    public string ReleaseNotes { get; init; } = string.Empty;
    public Uri? ReleasePageUri { get; init; }
}
