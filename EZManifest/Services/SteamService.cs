using SteamKit2;
using SteamKit2.CDN;
using System.Net.Http;

namespace EZManifest.Services;

internal static class SteamService
{
    private static SteamClient? _client;
    private static bool _isConnected;
    private static CallbackManager? _manager;

    public static async Task InitializeAsync()
    {
        if (_isConnected) return;

        _client = new SteamClient();
        _manager = new CallbackManager(_client);

        _client.Connect();

        var tcs = new TaskCompletionSource<bool>();

        _manager.Subscribe<SteamClient.ConnectedCallback>(_ =>
        {
            _client.GetHandler<SteamUser>()!.LogOnAnonymous();
        });

        _manager.Subscribe<SteamUser.LoggedOnCallback>(c =>
        {
            if (c.Result == EResult.OK)
            {
                _isConnected = true;
                tcs.SetResult(true);
            }
        });

        _ = Task.Run(() =>
        {
            while (!_isConnected)
            {
                _client.WaitForCallback(TimeSpan.FromSeconds(1));
                _manager.RunCallbacks();
            }
        });

        await tcs.Task;
    }

    public static async Task<string> DownloadManifestAsync(string depotId, string manifestId, string savePath)
    {
        if (!_isConnected) await InitializeAsync();

        var steamContent = _client!.GetHandler<SteamContent>()!;
        var servers = await steamContent.GetServersForSteamPipe();
        var server = servers.FirstOrDefault(s => s.Type == "CDN")
            ?? throw new InvalidOperationException("No Steam CDN server is available.");

        using var httpClient = new HttpClient();
        string url = $"http://{server.Host}/depot/{depotId}/manifest/{manifestId}/5";

        var response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        byte[] data = await response.Content.ReadAsByteArrayAsync();
        string fullPath = Path.Combine(savePath, $"{manifestId}.manifest");
        await File.WriteAllBytesAsync(fullPath, data);

        return fullPath;
    }
}
