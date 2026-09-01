namespace EZManifest.Models;

public sealed class ManifestArchiveResult
{
    public string ExtractionDirectory { get; init; } = string.Empty;
    public string LuaFilePath { get; init; } = string.Empty;
    public string AppId { get; init; } = string.Empty;
    public string LogoPath { get; init; } = string.Empty;
    public string CoverArtPath { get; init; } = string.Empty;
    public string HeroPath { get; init; } = string.Empty;
    public string IconPath { get; init; } = string.Empty;
}
