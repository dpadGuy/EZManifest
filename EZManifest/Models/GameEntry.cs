namespace EZManifest.Models;

public sealed class GameEntry
{
    public string AppId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string StartLocation { get; set; } = string.Empty;
    /// <summary>Install folder for this game (where files are downloaded).</summary>
    public string InstallPath { get; set; } = string.Empty;
}
