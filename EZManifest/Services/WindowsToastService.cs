using System.Runtime.InteropServices;
using System.Security;
using EZManifest.Models;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI.Notifications;

namespace EZManifest.Services;

public sealed class WindowsToastService
{
    public const string AppUserModelId = "dpadGuy.EZManifest";
    private const int MaxToastImageBytes = 180_000;

    private const string InstallToastGroup = "ezmanifest-install";
    private const string InstallToastTag = "install-complete";

    private readonly AppSettingsService _settingsService;
    private readonly SteamMetadataService _steamMetadata;
    private readonly WindowProvider _windowProvider;
    private readonly List<ToastNotification> _activeToasts = [];
    private bool _initialized;
    private bool _canShow;

    public WindowsToastService(
        AppSettingsService settingsService,
        SteamMetadataService steamMetadata,
        WindowProvider windowProvider)
    {
        _settingsService = settingsService;
        _steamMetadata = steamMetadata;
        _windowProvider = windowProvider;
    }

    public void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;

        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            RegisterToastProtocol();
            _ = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
            _canShow = true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Toast] Windows toast notifier unavailable ({ex.Message.Trim()})");
            _canShow = false;
        }
    }

    public async Task NotifyInstallCompleteAsync(
        string gameName,
        string? appId = null,
        string? coverArtPath = null)
    {
        AppSettings settings;
        try
        {
            settings = await _settingsService.LoadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Could not load notification settings");
            return;
        }

        if (!settings.NotifyOnInstallComplete)
            return;

        string name = string.IsNullOrWhiteSpace(gameName) ? "Game" : gameName.Trim();
        string? imagePath = await PrepareToastImageAsync(appId, coverArtPath);
        Show($"Install complete for {name}", imagePath);
    }

    private void Show(string title, string? imagePath)
    {
        Initialize();
        if (!_canShow)
            return;

        try
        {
            string? imageUri = ToFileUri(imagePath);
            AppLog.Write($"[Toast] title='{title}' image='{imagePath ?? "(none)"}' uri='{imageUri ?? "(none)"}'");

            string xml = imageUri is null
                ? $@"<toast activationType=""protocol"" launch=""ezmanifest:focus""><visual><binding template=""ToastGeneric""><text>{SecurityElement.Escape(title)}</text></binding></visual></toast>"
                : $@"<toast activationType=""protocol"" launch=""ezmanifest:focus""><visual><binding template=""ToastGeneric""><image placement=""appLogoOverride"" src=""{SecurityElement.Escape(imageUri)}""/><text>{SecurityElement.Escape(title)}</text></binding></visual></toast>";

            var document = new XmlDocument();
            document.LoadXml(xml);
            var toast = new ToastNotification(document)
            {
                Tag = InstallToastTag,
                Group = InstallToastGroup
            };
            toast.Activated += OnInstallToastActivated;
            toast.Dismissed += OnInstallToastDismissed;
            toast.Failed += OnInstallToastFailed;
            _activeToasts.Add(toast);
            ToastNotificationManager.CreateToastNotifier(AppUserModelId).Show(toast);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Windows notification failed");
        }
    }

    private void OnInstallToastActivated(ToastNotification toast, object args)
    {
        ReleaseToast(toast);
        RemoveInstallToastFromHistory();
        AppLog.Write("[Toast] Install notification clicked");
        _windowProvider.ActivateExistingWindow(maximize: true);
    }

    private void OnInstallToastDismissed(ToastNotification toast, ToastDismissedEventArgs args)
    {
        ReleaseToast(toast);
        AppLog.Write($"[Toast] Install notification dismissed ({args.Reason})");
    }

    private void OnInstallToastFailed(ToastNotification toast, ToastFailedEventArgs args)
    {
        ReleaseToast(toast);
        AppLog.Write(args.ErrorCode, "[Toast] Install notification failed");
    }

    private void ReleaseToast(ToastNotification toast)
    {
        toast.Activated -= OnInstallToastActivated;
        toast.Dismissed -= OnInstallToastDismissed;
        toast.Failed -= OnInstallToastFailed;
        _activeToasts.Remove(toast);
    }

    public static void ClearInstallToast() => RemoveInstallToastFromHistory();

    private static void RemoveInstallToastFromHistory()
    {
        try
        {
            ToastNotificationManager.History.Remove(InstallToastTag, InstallToastGroup, AppUserModelId);
        }
        catch (Exception ex)
        {
            AppLog.Write($"[Toast] Could not clear notification history: {ex.Message}");
        }
    }

    private static void RegisterToastProtocol()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            exe = Path.Combine(AppPaths.ExeDirectory, "EZManifest.exe");
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return;

        using RegistryKey root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\ezmanifest");
        root.SetValue(null, "URL:EZManifest Protocol");
        root.SetValue("URL Protocol", string.Empty);
        using RegistryKey command = root.CreateSubKey(@"shell\open\command");
        command.SetValue(null, $"\"{exe}\" {Program.ToastActivateArgument}");
    }

    private async Task<string?> PrepareToastImageAsync(string? appId, string? coverArtPath)
    {
        try
        {
            string? iconPath = SteamMetadataService.ResolveIconPath(coverArtPath, appId);
            if (!FileExists(iconPath) && !string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(iconPath))
                await _steamMetadata.DownloadIconAsync(appId, iconPath);

            string? source = FirstExisting(
                iconPath,
                Sibling(coverArtPath, "GameLogo.png"),
                coverArtPath);
            if (source is null)
            {
                AppLog.Write("[Toast] No game image found for notification");
                return null;
            }

            string cacheDir = Path.Combine(AppPaths.DataDirectory, "ToastIcons");
            Directory.CreateDirectory(cacheDir);
            string cachePath = Path.Combine(
                cacheDir,
                $"{SanitizeFileName(string.IsNullOrWhiteSpace(appId) ? "game" : appId)}.jpg");

            var info = new FileInfo(source);
            if (info.Length <= MaxToastImageBytes &&
                (source.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                 source.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)))
            {
                File.Copy(source, cachePath, overwrite: true);
                return cachePath;
            }

            if (await WriteSmallJpegAsync(source, cachePath))
                return cachePath;

            if (info.Length <= MaxToastImageBytes)
            {
                File.Copy(source, cachePath, overwrite: true);
                return cachePath;
            }

            AppLog.Write($"[Toast] Image too large for toast ({info.Length} bytes): {source}");
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Toast image prepare failed");
            return null;
        }
    }

    private static async Task<bool> WriteSmallJpegAsync(string sourcePath, string destPath)
    {
        try
        {
            StorageFile source = await StorageFile.GetFileFromPathAsync(sourcePath);
            using var input = await source.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);

            const uint side = 128;
            var transform = new BitmapTransform
            {
                ScaledWidth = side,
                ScaledHeight = side,
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            PixelDataProvider pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            string? directory = Path.GetDirectoryName(destPath);
            if (string.IsNullOrWhiteSpace(directory))
                return false;

            Directory.CreateDirectory(directory);
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(directory);
            StorageFile dest = await folder.CreateFileAsync(
                Path.GetFileName(destPath),
                CreationCollisionOption.ReplaceExisting);
            using var output = await dest.OpenAsync(FileAccessMode.ReadWrite);
            output.Size = 0;
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                side,
                side,
                96,
                96,
                pixels.DetachPixelData());
            await encoder.FlushAsync();
            return FileExists(destPath);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Toast image shrink failed");
            return false;
        }
    }

    private static string? FirstExisting(params string?[] paths)
    {
        foreach (string? path in paths)
        {
            if (FileExists(path))
                return path;
        }

        return null;
    }

    private static string? Sibling(string? path, string fileName)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string? directory = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, fileName);
    }

    private static bool FileExists(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length > 0;

    private static string? ToFileUri(string? path)
    {
        if (!FileExists(path))
            return null;

        string full = Path.GetFullPath(path!).Replace('\\', '/');
        if (full.Length >= 2 && full[1] == ':')
            return "file:///" + Uri.EscapeDataString(full).Replace("%2F", "/").Replace("%3A", ":");

        return new UriBuilder
        {
            Scheme = Uri.UriSchemeFile,
            Host = string.Empty,
            Path = Path.GetFullPath(path!)
        }.Uri.AbsoluteUri;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "game" : name;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);
}
