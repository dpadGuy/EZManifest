using System.Diagnostics;

namespace EZManifest.Services;

public sealed class PostDownloadService
{
    public string? GetCliExecutablePath()
    {
        string candidate1 = Path.Combine(AppPaths.ExeDirectory, "SteamAutoCrack.CLI", "SteamAutoCrack.CLI.exe");
        if (File.Exists(candidate1))
            return candidate1;

        string candidate2 = Path.Combine(AppContext.BaseDirectory, "SteamAutoCrack.CLI", "SteamAutoCrack.CLI.exe");
        if (File.Exists(candidate2))
            return candidate2;

        return null;
    }

    public async Task<int> RunPostDownloadCommandAsync(
        string gameName,
        string appId,
        string installPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installPath) || string.IsNullOrWhiteSpace(appId))
        {
            AppLog.Write(
                $"[PostDownload] Missing required parameters for '{gameName}' (appId='{appId}', installPath='{installPath}') — skipping.");
            throw new ArgumentException("AppID and game install path are required.");
        }

        string? fileName = GetCliExecutablePath();
        if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName))
        {
            string expectedPath = Path.Combine(AppPaths.ExeDirectory, "SteamAutoCrack.CLI", "SteamAutoCrack.CLI.exe");
            AppLog.Write($"[PostDownload] Command exe not found: {expectedPath}");
            throw new FileNotFoundException($"SteamAutoCrack.CLI.exe was not found at:\n{expectedPath}");
        }

        string arguments = $"crack \"{installPath}\" --appid {appId}";

        AppLog.Write($"[PostDownload] Starting for '{gameName}' appId={appId}");
        AppLog.Write($"[PostDownload] {fileName} {arguments}");
        AppLog.Write($"[PostDownload] WorkingDirectory={installPath}");

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = installPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exitTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                AppLog.Write($"[PostDownload] {e.Data}");
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                AppLog.Write($"[PostDownload:err] {e.Data}");
        };
        process.Exited += (_, _) => exitTcs.TrySetResult();

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using (cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            exitTcs.TrySetCanceled(cancellationToken);
        }))
        {
            await exitTcs.Task;
        }

        AppLog.Write($"[PostDownload] Exit code {process.ExitCode} for '{gameName}'");
        return process.ExitCode;
    }
}
