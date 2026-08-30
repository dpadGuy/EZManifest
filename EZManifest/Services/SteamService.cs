using System.Net.Http;
using SteamKit2;

namespace EZManifest.Services;

internal static class SteamService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static SteamClient? _client;
    private static CallbackManager? _manager;
    private static CancellationTokenSource? _pumpCts;
    private static bool _isConnected;

    public static async Task InitializeAsync()
    {
        if (_isConnected)
            return;

        await Gate.WaitAsync();
        try
        {
            if (_isConnected)
                return;

            _client = new SteamClient();
            _manager = new CallbackManager(_client);
            _pumpCts = new CancellationTokenSource();

            var loggedOn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _manager.Subscribe<SteamClient.ConnectedCallback>(_ =>
            {
                _client.GetHandler<SteamUser>()!.LogOnAnonymous();
            });

            _manager.Subscribe<SteamUser.LoggedOnCallback>(callback =>
            {
                if (callback.Result == EResult.OK)
                {
                    _isConnected = true;
                    loggedOn.TrySetResult(true);
                }
                else
                {
                    loggedOn.TrySetException(
                        new InvalidOperationException($"Steam anonymous login failed: {callback.Result}"));
                }
            });

            _manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
            {
                _isConnected = false;
            });

            CancellationToken pumpToken = _pumpCts.Token;
            _ = Task.Run(() =>
            {
                while (!pumpToken.IsCancellationRequested)
                    _manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(50));
            }, CancellationToken.None);

            _client.Connect();
            await loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(25));
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<IReadOnlyDictionary<uint, KeyValue>> GetAppInfosAsync(
        IEnumerable<uint> appIds,
        CancellationToken cancellationToken = default)
    {
        var ids = appIds.Where(id => id != 0).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<uint, KeyValue>();

        // Own connection + callback pump on a worker thread. The shared SteamService
        // client is not safe to use from the WinUI dispatcher.
        return await Task.Run(() => FetchAppInfosIsolatedAsync(ids, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyDictionary<uint, KeyValue>> FetchAppInfosIsolatedAsync(
        List<uint> ids,
        CancellationToken cancellationToken)
    {
        var client = new SteamClient();
        var manager = new CallbackManager(client);
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var loggedOn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        manager.Subscribe<SteamClient.ConnectedCallback>(_ =>
        {
            client.GetHandler<SteamUser>()!.LogOnAnonymous();
        });
        manager.Subscribe<SteamUser.LoggedOnCallback>(callback =>
        {
            if (callback.Result == EResult.OK)
                loggedOn.TrySetResult(true);
            else
                loggedOn.TrySetException(
                    new InvalidOperationException($"Steam anonymous login failed: {callback.Result}"));
        });
        manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            loggedOn.TrySetException(new InvalidOperationException("Disconnected from Steam."));
        });

        var pump = Task.Run(() =>
        {
            while (!pumpCts.IsCancellationRequested)
                manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(50));
        }, CancellationToken.None);

        try
        {
            client.Connect();
            await loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);

            var steamApps = client.GetHandler<SteamApps>()
                ?? throw new InvalidOperationException("SteamApps handler is unavailable.");
            var requests = ids.Select(id => new SteamApps.PICSRequest(id)).ToList();
            var resultSet = await steamApps
                .PICSGetProductInfo(requests, Array.Empty<SteamApps.PICSRequest>())
                .ToTask()
                .WaitAsync(cancellationToken);

            var map = new Dictionary<uint, KeyValue>();
            if (resultSet.Results is null)
                return map;

            foreach (var callback in resultSet.Results)
            {
                foreach (var (id, info) in callback.Apps)
                    map[id] = info.KeyValues;
            }

            AppLog.Write($"[DepotMetadata] Steam PICS returned {map.Count} app(s)");
            return map;
        }
        finally
        {
            try
            {
                client.GetHandler<SteamUser>()?.LogOff();
            }
            catch
            {
            }

            pumpCts.Cancel();
            client.Disconnect();
            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }
    }

    public static async Task<string> DownloadManifestAsync(string depotId, string manifestId, string savePath)
    {
        if (!_isConnected)
            await InitializeAsync();

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
