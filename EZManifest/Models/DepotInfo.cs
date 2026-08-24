namespace EZManifest.Models;

public sealed class DepotInfo
{
    public string DepotId { get; set; } = string.Empty;
    public string ManifestId { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string HexKey { get; set; } = string.Empty;
}
