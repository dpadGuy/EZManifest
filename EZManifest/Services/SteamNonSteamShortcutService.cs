using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using EZManifest.Models;
using Microsoft.Win32;

namespace EZManifest.Services;

public sealed record SteamShortcutAddResult(
    int AccountsUpdated,
    int AlreadyPresent,
    int AccountCount,
    bool SteamWasRunning);

public sealed class SteamNonSteamShortcutService
{
    private readonly SteamMetadataService _steamMetadata;

    public SteamNonSteamShortcutService(SteamMetadataService steamMetadata) =>
        _steamMetadata = steamMetadata;

    public static bool IsSteamRunning() =>
        Process.GetProcessesByName("steam").Length > 0;

    public static bool IsSteamUiRunning()
    {
        Process[] steam = Process.GetProcessesByName("steam");
        Process[] helpers = Process.GetProcessesByName("steamwebhelper");
        try
        {
            if (steam.Length == 0 || helpers.Length == 0)
                return false;

            var steamIds = steam.Select(process => process.Id).ToHashSet();
            return helpers.Any(helper => IsInSteamProcessTree(helper, steamIds));
        }
        finally
        {
            foreach (Process process in steam)
                process.Dispose();
            foreach (Process process in helpers)
                process.Dispose();
        }
    }

    public async Task CloseSteamAsync(CancellationToken cancellationToken = default)
    {
        string? steamExe = FindSteamExe();
        if (!string.IsNullOrWhiteSpace(steamExe) && File.Exists(steamExe))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = steamExe,
                    Arguments = "-shutdown",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLog.Write(ex, "[SteamShortcut] steam -shutdown failed");
            }
        }

        var started = Stopwatch.StartNew();
        while (IsSteamRunning() && started.Elapsed < TimeSpan.FromSeconds(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(400, cancellationToken);
        }

        if (IsSteamRunning())
            throw new InvalidOperationException("Steam is still running. Close it, then try again.");
    }

    public async Task RestartSteamAsync(CancellationToken cancellationToken = default)
    {
        (_, int? steamPid) = TryGetSteamUiProcess();
        AppLog.Write($"[SteamShortcut] Restarting Steam via steam://open/library pid={steamPid?.ToString() ?? "none"}");
        if (steamPid is int pid)
            await KillSteamUiAsync(pid, cancellationToken);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c start \"\" steam://open/library",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static (string? Path, int? Pid) TryGetSteamUiProcess()
    {
        Process[] steam = Process.GetProcessesByName("steam");
        Process[] helpers = Process.GetProcessesByName("steamwebhelper");
        try
        {
            if (steam.Length == 0 || helpers.Length == 0)
                return (null, null);

            var steamIds = steam.Select(process => process.Id).ToHashSet();
            var uiIds = helpers
                .Select(helper => FindSteamAncestorId(helper, steamIds))
                .Where(id => id is > 0)
                .Select(id => id!.Value)
                .ToHashSet();
            if (uiIds.Count == 0)
                return (null, null);

            foreach (Process process in steam)
            {
                if (!uiIds.Contains(process.Id))
                    continue;

                try
                {
                    string? path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) &&
                        path.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(path))
                        return (path, process.Id);
                }
                catch (Exception ex)
                {
                    AppLog.Write($"[SteamShortcut] Could not read steam start path: {ex.Message}");
                }
            }

            return (null, uiIds.First());
        }
        finally
        {
            foreach (Process process in steam)
                process.Dispose();
            foreach (Process process in helpers)
                process.Dispose();
        }
    }

    private static async Task KillSteamUiAsync(int steamPid, CancellationToken cancellationToken)
    {
        var steamIds = new HashSet<int> { steamPid };
        foreach (Process helper in Process.GetProcessesByName("steamwebhelper"))
        {
            try
            {
                if (FindSteamAncestorId(helper, steamIds) is > 0)
                    helper.Kill();
            }
            catch (Exception ex)
            {
                AppLog.Write($"[SteamShortcut] Could not end steamwebhelper ({helper.Id}): {ex.Message}");
            }
            finally
            {
                helper.Dispose();
            }
        }

        try
        {
            using Process steam = Process.GetProcessById(steamPid);
            steam.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Already exited.
        }
        catch (Exception ex)
        {
            AppLog.Write($"[SteamShortcut] Could not end steam ({steamPid}): {ex.Message}");
        }

        var started = Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using Process still = Process.GetProcessById(steamPid);
                if (still.HasExited)
                    return;
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(400, cancellationToken);
        }

        throw new InvalidOperationException("Steam is still running. Close it, then try again.");
    }

    public async Task<SteamShortcutAddResult> AddToAllAccountsAsync(
        GameEntry game,
        string exePath,
        CancellationToken cancellationToken = default)
    {
        string steamRoot = FindSteamRoot()
            ?? throw new DirectoryNotFoundException("Steam was not found. Install Steam or start it once, then try again.");

        string userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata))
            throw new DirectoryNotFoundException($"Steam userdata was not found:\n{userdata}");

        string[] accounts = Directory.GetDirectories(userdata)
            .Where(path => Path.GetFileName(path).All(char.IsDigit))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (accounts.Length == 0)
            throw new DirectoryNotFoundException("No Steam accounts were found in userdata.");

        exePath = Path.GetFullPath(exePath);
        if (!File.Exists(exePath))
            throw new FileNotFoundException("Game executable was not found.", exePath);

        string startDir = Path.GetDirectoryName(exePath) ?? string.Empty;
        if (!startDir.EndsWith(Path.DirectorySeparatorChar))
            startDir += Path.DirectorySeparatorChar;

        string quotedExe = QuotePath(exePath);
        string quotedStart = QuotePath(startDir);
        string appName = string.IsNullOrWhiteSpace(game.Name) ? Path.GetFileNameWithoutExtension(exePath) : game.Name;
        uint appId = ShortcutAppId(quotedExe, appName);
        int appIdSigned = unchecked((int)appId);
        string launchOptions = game.LaunchOptions ?? string.Empty;
        bool steamWasRunning = IsSteamUiRunning();

        SteamShortcutArtwork art = await PrepareArtworkAsync(game, cancellationToken);

        int updated = 0;
        int already = 0;
        foreach (string account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string configDir = Path.Combine(account, "config");
            string shortcutsPath = Path.Combine(configDir, "shortcuts.vdf");
            SteamShortcutsVdf.Node root = SteamShortcutsVdf.LoadOrCreate(shortcutsPath);

            SteamShortcutsVdf.Node? existing = FindExisting(root, quotedExe, appName);
            if (existing is not null)
            {
                existing.SetInt("appid", appIdSigned);
                existing.SetString("icon", art.IconPath);
                existing.SetString("StartDir", quotedStart);
                existing.SetString("LaunchOptions", launchOptions);
                already++;
            }
            else
            {
                int index = NextIndex(root);
                root.Children.Add(SteamShortcutsVdf.NewShortcut(
                    index,
                    appIdSigned,
                    appName,
                    quotedExe,
                    quotedStart,
                    art.IconPath,
                    launchOptions));
            }

            SteamShortcutsVdf.Save(shortcutsPath, root);
            CopyGridArt(Path.Combine(configDir, "grid"), appId, art);
            updated++;
            AppLog.Write($"[SteamShortcut] '{appName}' → account {Path.GetFileName(account)} appId={appId}");
        }

        return new SteamShortcutAddResult(updated, already, accounts.Length, steamWasRunning);
    }

    public Task RemoveFromAllAccountsAsync(GameEntry game, CancellationToken cancellationToken = default)
    {
        try
        {
            RemoveFromAllAccounts(game, cancellationToken);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"[SteamShortcut] Remove skipped for '{game.Name}'");
        }

        return Task.CompletedTask;
    }

    private void RemoveFromAllAccounts(GameEntry game, CancellationToken cancellationToken)
    {
        string? steamRoot = FindSteamRoot();
        if (string.IsNullOrWhiteSpace(steamRoot))
            return;

        string userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata))
            return;

        string appName = game.Name ?? string.Empty;
        string exePath = string.IsNullOrWhiteSpace(game.StartLocation)
            ? string.Empty
            : Path.GetFullPath(game.StartLocation);

        foreach (string account in Directory.GetDirectories(userdata).Where(path => Path.GetFileName(path).All(char.IsDigit)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string shortcutsPath = Path.Combine(account, "config", "shortcuts.vdf");
            if (!File.Exists(shortcutsPath))
                continue;

            SteamShortcutsVdf.Node root = SteamShortcutsVdf.LoadOrCreate(shortcutsPath);
            var removed = root.Children
                .Where(child => child.Type == 0x00 && MatchesShortcut(child, appName, exePath))
                .ToList();
            if (removed.Count == 0)
                continue;

            foreach (SteamShortcutsVdf.Node node in removed)
            {
                uint appId = unchecked((uint)node.GetInt("appid"));
                if (appId == 0 && !string.IsNullOrWhiteSpace(exePath) && !string.IsNullOrWhiteSpace(appName))
                    appId = ShortcutAppId(QuotePath(exePath), appName);

                root.Children.Remove(node);
                if (appId != 0)
                    DeleteGridArt(Path.Combine(account, "config", "grid"), appId);
            }

            SteamShortcutsVdf.Save(shortcutsPath, root);
            AppLog.Write(
                $"[SteamShortcut] Removed {removed.Count} shortcut(s) for '{appName}' " +
                $"from account {Path.GetFileName(account)}");
        }
    }

    private static bool MatchesShortcut(SteamShortcutsVdf.Node node, string appName, string exePath)
    {
        string name = node.GetString("AppName");
        string exe = NormalizeExe(node.GetString("Exe"));
        if (!string.IsNullOrWhiteSpace(appName) &&
            name.Equals(appName, StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(exePath) &&
               exe.Equals(NormalizeExe(exePath), StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteGridArt(string gridDir, uint appId)
    {
        if (!Directory.Exists(gridDir))
            return;

        ulong wideId = ((ulong)appId << 32) | 0x02000000UL;
        foreach (string id in new[] { appId.ToString(), wideId.ToString() })
        {
            foreach (string file in Directory.EnumerateFiles(gridDir, $"{id}*"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    AppLog.Write($"[SteamShortcut] Could not delete grid art '{file}': {ex.Message}");
                }
            }
        }
    }

    private async Task<SteamShortcutArtwork> PrepareArtworkAsync(GameEntry game, CancellationToken cancellationToken)
    {
        string? cover = ExistingFile(game.Image);
        string? icon = ExistingFile(SteamMetadataService.ResolveIconPath(game.Image, game.AppId));
        string? hero = ExistingFile(SteamMetadataService.ResolveHeroPath(game.Image));
        string? logo = ExistingFile(SteamMetadataService.ResolveLogoPath(game.Image));
        string? header = ExistingFile(SteamMetadataService.ResolveHeaderPath(game.Image));

        if (!string.IsNullOrWhiteSpace(game.AppId))
        {
            try
            {
                string? assets = Path.GetDirectoryName(game.Image);
                if (string.IsNullOrWhiteSpace(assets) && !string.IsNullOrWhiteSpace(game.AppId))
                    assets = Path.Combine(AppPaths.ManifestsDirectory, $"undefined_{game.AppId}", "Assets");

                if (!string.IsNullOrWhiteSpace(assets))
                {
                    Directory.CreateDirectory(assets);
                    header ??= Path.Combine(assets, "Header.jpg");
                    hero ??= Path.Combine(assets, "LibraryHero.jpg");
                    icon ??= Path.Combine(assets, "GameIcon.jpg");
                    logo ??= Path.Combine(assets, "GameLogo.png");
                    string portrait = Path.Combine(assets, "GridPortrait.jpg");
                    await _steamMetadata.EnsureHighResHeroAsync(game.AppId, hero, cancellationToken);
                    await _steamMetadata.EnsureHighResPortraitAsync(game.AppId, portrait, cancellationToken);
                    await _steamMetadata.DownloadHeaderAsync(game.AppId, header, cancellationToken);
                    await _steamMetadata.DownloadIconAsync(game.AppId, icon, cancellationToken);
                    await _steamMetadata.DownloadLogoAsync(game.AppId, logo, cancellationToken);
                    if (File.Exists(portrait))
                        cover = portrait;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write(ex, "[SteamShortcut] Artwork scrape failed");
            }

            header = ExistingFile(header);
            hero = ExistingFile(hero);
            icon = ExistingFile(icon);
            logo = ExistingFile(logo);
            cover = ExistingFile(cover) ?? ExistingFile(game.Image);
        }

        return new SteamShortcutArtwork(
            cover,
            hero ?? header,
            hero,
            logo,
            icon ?? cover ?? string.Empty);
    }

    private static void CopyGridArt(string gridDir, uint appId, SteamShortcutArtwork art)
    {
        Directory.CreateDirectory(gridDir);
        ulong wideId = ((ulong)appId << 32) | 0x02000000UL;
        uint[] ids32 = [appId];
        ulong[] ids64 = [wideId];

        foreach (uint id in ids32)
        {
            CopyAs(art.Portrait, Path.Combine(gridDir, $"{id}p{Ext(art.Portrait)}"));
            CopyAs(art.Landscape, Path.Combine(gridDir, $"{id}{Ext(art.Landscape)}"));
            CopyAs(art.Hero, Path.Combine(gridDir, $"{id}_hero{Ext(art.Hero)}"));
            CopyAs(art.Logo, Path.Combine(gridDir, $"{id}_logo{Ext(art.Logo)}"));
            CopyAs(art.IconPath, Path.Combine(gridDir, $"{id}_icon{Ext(art.IconPath)}"));
        }

        foreach (ulong id in ids64)
        {
            CopyAs(art.Portrait, Path.Combine(gridDir, $"{id}p{Ext(art.Portrait)}"));
            CopyAs(art.Landscape, Path.Combine(gridDir, $"{id}{Ext(art.Landscape)}"));
            CopyAs(art.Hero, Path.Combine(gridDir, $"{id}_hero{Ext(art.Hero)}"));
            CopyAs(art.Logo, Path.Combine(gridDir, $"{id}_logo{Ext(art.Logo)}"));
        }
    }

    private static void CopyAs(string? source, string dest)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return;

        try
        {
            File.Copy(source, dest, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Write($"[SteamShortcut] Could not copy '{source}' → '{dest}': {ex.Message}");
        }
    }

    private static string Ext(string? path)
    {
        string ext = Path.GetExtension(path ?? string.Empty);
        return string.IsNullOrWhiteSpace(ext) ? ".jpg" : ext;
    }

    private static string? ExistingFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;

    private static SteamShortcutsVdf.Node? FindExisting(SteamShortcutsVdf.Node root, string quotedExe, string appName)
    {
        foreach (SteamShortcutsVdf.Node child in root.Children)
        {
            if (child.Type != 0x00)
                continue;

            string name = child.GetString("AppName");
            string exe = child.GetString("Exe");
            if (name.Equals(appName, StringComparison.OrdinalIgnoreCase) &&
                NormalizeExe(exe).Equals(NormalizeExe(quotedExe), StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    private static int NextIndex(SteamShortcutsVdf.Node root)
    {
        int max = -1;
        foreach (SteamShortcutsVdf.Node child in root.Children)
        {
            if (int.TryParse(child.Name, out int index) && index > max)
                max = index;
        }

        return max + 1;
    }

    private static string NormalizeExe(string exe) =>
        exe.Trim().Trim('"');

    private static string QuotePath(string path)
    {
        path = path.Trim();
        if (path.StartsWith('"') && path.EndsWith('"'))
            return path;
        return $"\"{path}\"";
    }

    public static uint ShortcutAppId(string quotedExe, string appName)
    {
        uint crc = Crc32(Encoding.UTF8.GetBytes(quotedExe + appName));
        return crc | 0x80000000u;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }

    private static string? FindSteamRoot()
    {
        string?[] registry =
        [
            ReadSteamPath(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            ReadSteamPath(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            ReadSteamPath(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath")
        ];

        foreach (string? candidate in registry.Concat(
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
        ]))
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            string root = candidate.Replace('/', Path.DirectorySeparatorChar);
            if (Directory.Exists(Path.Combine(root, "userdata")) || File.Exists(Path.Combine(root, "steam.exe")))
                return root;
        }

        return null;
    }

    private static string? FindSteamExe()
    {
        string? root = FindSteamRoot();
        if (string.IsNullOrWhiteSpace(root))
            return null;

        string exe = Path.Combine(root, "steam.exe");
        return File.Exists(exe) ? exe : null;
    }

    private static string? ReadSteamPath(RegistryKey hive, string keyPath, string valueName)
    {
        try
        {
            using RegistryKey? key = hive.OpenSubKey(keyPath);
            object? value = key?.GetValue(valueName);
            return value?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsInSteamProcessTree(Process process, HashSet<int> steamIds) =>
        FindSteamAncestorId(process, steamIds) is > 0;

    private static int? FindSteamAncestorId(Process process, HashSet<int> steamIds)
    {
        int? parentId = TryGetParentProcessId(process);
        for (int depth = 0; depth < 8 && parentId is > 0; depth++)
        {
            if (steamIds.Contains(parentId.Value))
                return parentId.Value;

            try
            {
                using Process parent = Process.GetProcessById(parentId.Value);
                parentId = TryGetParentProcessId(parent);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static int? TryGetParentProcessId(Process process)
    {
        try
        {
            var info = new ProcessBasicInformation();
            int status = NtQueryInformationProcess(
                process.Handle,
                0,
                ref info,
                Marshal.SizeOf<ProcessBasicInformation>(),
                out _);
            if (status != 0)
                return null;

            return info.InheritedFromUniqueProcessId.ToInt32();
        }
        catch
        {
            return null;
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    private readonly record struct SteamShortcutArtwork(
        string? Portrait,
        string? Landscape,
        string? Hero,
        string? Logo,
        string IconPath);
}
