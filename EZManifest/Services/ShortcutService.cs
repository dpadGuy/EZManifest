using System.Runtime.InteropServices;
using System.Text;

namespace EZManifest.Services;

public sealed class ShortcutService
{
    public string CreateDesktopShortcut(
        string targetExePath,
        string shortcutTitle,
        string? workingDirectory = null,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(targetExePath))
            throw new ArgumentException("Target executable path is required.", nameof(targetExePath));

        if (string.IsNullOrWhiteSpace(shortcutTitle))
            shortcutTitle = Path.GetFileNameWithoutExtension(targetExePath);

        string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktopPath) || !Directory.Exists(desktopPath))
            desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        string sanitizedTitle = SanitizeFileName(shortcutTitle);
        string shortcutPath = Path.Combine(desktopPath, $"{sanitizedTitle}.lnk");

        workingDirectory ??= Path.GetDirectoryName(targetExePath) ?? string.Empty;

        var link = (IShellLinkW)new ShellLink();
        link.SetPath(targetExePath);
        link.SetWorkingDirectory(workingDirectory);
        if (!string.IsNullOrWhiteSpace(description))
            link.SetDescription(description);

        var file = (IPersistFile)link;
        file.Save(shortcutPath, false);

        return shortcutPath;
    }

    public bool RemoveDesktopShortcut(string shortcutTitle)
    {
        if (string.IsNullOrWhiteSpace(shortcutTitle))
            return false;

        try
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktopPath) || !Directory.Exists(desktopPath))
                desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            string sanitizedTitle = SanitizeFileName(shortcutTitle);
            string shortcutPath = Path.Combine(desktopPath, $"{sanitizedTitle}.lnk");

            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
                AppLog.Write($"[Shortcut] Removed desktop shortcut: {shortcutPath}");
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"[Shortcut] Ignored error while removing shortcut for '{shortcutTitle}'");
        }

        return false;
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (!invalid.Contains(c))
                sb.Append(c);
            else
                sb.Append(' ');
        }

        string result = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? "GameShortcut" : result;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder ppszFileName);
    }
}
