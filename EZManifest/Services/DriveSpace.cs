namespace EZManifest.Services;

internal static class DriveSpace
{
    public const long SafetyReserveBytes = 64L * 1024 * 1024;

    public static bool TryGetAvailableBytes(string path, out long availableBytes)
    {
        availableBytes = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string full = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                return false;

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return false;

            availableBytes = drive.AvailableFreeSpace;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string DriveName(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(root))
                return root.TrimEnd('\\');
        }
        catch
        {
        }

        return "the download drive";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            bytes = 0;

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
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
