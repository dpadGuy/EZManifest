namespace EZManifest.Services;

internal static class GeForceNowHost
{
    public const string MarkerPath = @"C:\asgard";
    public const string DefaultGamesPath = @"I:\Games";

    public static bool IsDetected => Directory.Exists(MarkerPath);

    public static bool TryGetDefaultInstallPath(out string path)
    {
        path = DefaultGamesPath;
        if (!IsDetected)
            return false;

        try
        {
            if (Directory.Exists(@"I:\"))
                Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Could not create I:\\Games on GeForce Now host");
        }

        return true;
    }
}
