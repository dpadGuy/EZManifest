using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Channels;
using EZManifest.Models;
using Microsoft.Win32.SafeHandles;
using SteamKit2;
using SteamKit2.CDN;

namespace EZManifest.Services;

public class DownloadJob
{
    public required DepotManifest.FileData File { get; set; }
    public required byte[] DepotKey { get; set; }
    public required string DepotId { get; set; }
}

public static class GameDownload
{
    // High enough to use multi-gigabit links; Steam CDN still rate-limits per host.
    // Progress reports are throttled so this concurrency does not freeze the UI.
    private const int MaxConcurrentFiles = 24;
    private const int MaxConcurrentChunks = 64;
    private const int MaxConnectionsPerServer = 32;
    private const int MaxRetriesPerChunk = 12;
    private const int ProgressReportMinIntervalMs = 100;
    private const string ContentServerDirectoryUrlFormat =
        "https://api.steampowered.com/IContentServerDirectoryService/GetServersForSteamPipe/v1/?cell_id={0}&max_servers=80";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim _chunkGate = new(MaxConcurrentChunks, MaxConcurrentChunks);
    private static int _nextServerIndex = -1;

    public static async Task BatchEngineStart(
        List<DepotInfo> depots,
        Dictionary<string, byte[]> depotKeys,
        string downloadLocation,
        IProgress<DownloadProgress> progressReporter,
        Func<CancellationToken, Task> waitIfPaused,
        CancellationToken cancellationToken,
        int cdnCellId = 0)
    {
        var servers = await GetCdnServers(cdnCellId, cancellationToken);
        var channel = Channel.CreateUnbounded<DownloadJob>();

        // Pre-scan totals so progress % is meaningful once workers start.
        long totalBytes = 0;
        var pendingJobs = new List<DownloadJob>();
        foreach (var depot in depots)
        {
            if (!depotKeys.ContainsKey(depot.DepotId)) continue;

            byte[] key = depotKeys[depot.DepotId];
            cancellationToken.ThrowIfCancellationRequested();
            byte[] manifestData = await File.ReadAllBytesAsync(depot.ManifestPath, cancellationToken);
            var manifest = DepotManifest.Deserialize(manifestData);
            manifest.DecryptFilenames(key);

            var files = manifest.Files!.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory));
            foreach (var file in files)
            {
                pendingJobs.Add(new DownloadJob { File = file, DepotKey = key, DepotId = depot.DepotId });
                totalBytes += file.Chunks.Sum(chunk => (long)chunk.UncompressedLength);
            }
        }

        progressReporter.Report(new DownloadProgress(0, totalBytes, 0));

        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = MaxConnectionsPerServer,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true
        };
        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Valve/Steam HTTP Client 1.0");

        long processedBytes = 0;
        long networkBytes = 0;
        long lastProgressReportMs = 0;

        void ReportProgress(bool force = false)
        {
            long nowMs = Environment.TickCount64;
            if (!force)
            {
                long previous = Interlocked.Read(ref lastProgressReportMs);
                if (nowMs - previous < ProgressReportMinIntervalMs)
                    return;
                if (Interlocked.CompareExchange(ref lastProgressReportMs, nowMs, previous) != previous)
                    return;
            }
            else
            {
                Interlocked.Exchange(ref lastProgressReportMs, nowMs);
            }

            progressReporter.Report(new DownloadProgress(
                Interlocked.Read(ref processedBytes),
                totalBytes,
                Interlocked.Read(ref networkBytes)));
        }

        async Task Worker()
        {
            try
            {
                await foreach (var job in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    await waitIfPaused(cancellationToken);
                    await DownloadAndProcessFile(
                        job,
                        downloadLocation,
                        httpClient,
                        servers,
                        waitIfPaused,
                        (writtenBytes, receivedNetworkBytes) =>
                        {
                            if (writtenBytes > 0)
                                Interlocked.Add(ref processedBytes, writtenBytes);
                            if (receivedNetworkBytes > 0)
                                Interlocked.Add(ref networkBytes, receivedNetworkBytes);
                            ReportProgress();
                        },
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cooperative cancel — avoid Task.WhenAll aggregating dozens of cancels.
            }
        }

        // Start workers before queueing so download begins immediately.
        var workers = Enumerable.Range(0, MaxConcurrentFiles).Select(_ => Worker()).ToArray();

        try
        {
            foreach (var job in pendingJobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await channel.Writer.WriteAsync(job, cancellationToken);
            }
        }
        finally
        {
            channel.Writer.Complete();
        }

        await Task.WhenAll(workers);
        ReportProgress(force: true);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task DownloadAndProcessFile(
        DownloadJob job,
        string baseDir,
        HttpClient client,
        List<string> servers,
        Func<CancellationToken, Task> waitIfPaused,
        Action<int, int> reportChunkProgress,
        CancellationToken cancellationToken)
    {
        string safePath = Path.Combine(job.File.FileName.Split('/', '\\'));
        string targetPath = Path.Combine(baseDir, safePath);

        string dir = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(dir);

        var fileLock = _fileLocks.GetOrAdd(targetPath, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken);

        try
        {
            long fileSize = job.File.Chunks.Sum(chunk => (long)chunk.UncompressedLength);
            // Prefer declared size when present so sparse offsets still fit.
            if (job.File.TotalSize > 0)
                fileSize = Math.Max(fileSize, (long)job.File.TotalSize);

            using SafeFileHandle handle = File.OpenHandle(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            RandomAccess.SetLength(handle, fileSize);

            var chunks = job.File.Chunks.ToArray();
            var chunkTasks = chunks.Select(chunk => DownloadChunkIgnoringCancelAsync(
                job,
                chunk,
                handle,
                client,
                servers,
                waitIfPaused,
                reportChunkProgress,
                cancellationToken));

            await Task.WhenAll(chunkTasks);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            fileLock.Release();
        }
    }

    private static async Task DownloadChunkIgnoringCancelAsync(
        DownloadJob job,
        DepotManifest.ChunkData chunk,
        SafeFileHandle handle,
        HttpClient client,
        List<string> servers,
        Func<CancellationToken, Task> waitIfPaused,
        Action<int, int> reportChunkProgress,
        CancellationToken cancellationToken)
    {
        try
        {
            await DownloadAndWriteChunkAsync(
                job,
                chunk,
                handle,
                client,
                servers,
                waitIfPaused,
                reportChunkProgress,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Swallow per-chunk cancel so the debugger isn't flooded.
        }
    }

    private static async Task DownloadAndWriteChunkAsync(
        DownloadJob job,
        DepotManifest.ChunkData chunk,
        SafeFileHandle handle,
        HttpClient client,
        List<string> servers,
        Func<CancellationToken, Task> waitIfPaused,
        Action<int, int> reportChunkProgress,
        CancellationToken cancellationToken)
    {
        await waitIfPaused(cancellationToken);
        await _chunkGate.WaitAsync(cancellationToken);

        try
        {
            await waitIfPaused(cancellationToken);

            byte[]? chunkId = chunk.ChunkID
                ?? throw new InvalidDataException($"Chunk ID missing for {job.File.FileName}.");
            string chunkHex = Convert.ToHexString(chunkId).ToLowerInvariant();
            int firstServerIndex = (int)((uint)Interlocked.Increment(ref _nextServerIndex) % (uint)servers.Count);
            Exception? lastError = null;

            for (int attempt = 0; attempt < MaxRetriesPerChunk; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string server = servers[(firstServerIndex + attempt) % servers.Count];
                string url = $"http://{server}/depot/{job.DepotId}/chunk/{chunkHex}";

                try
                {
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        lastError = new HttpRequestException(
                            $"CDN {server} returned {(int)response.StatusCode} ({response.ReasonPhrase}) for chunk {chunkHex}.");
                        await DelayBeforeRetryAsync(attempt, response.StatusCode, cancellationToken);
                        continue;
                    }

                    byte[] encrypted = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    // Count CDN payload immediately so Mbps reflects download, not disk write.
                    reportChunkProgress(0, encrypted.Length);

                    byte[] buffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength + 65536);

                    try
                    {
                        int written = DepotChunk.Process(chunk, encrypted, buffer, job.DepotKey);
                        await RandomAccess.WriteAsync(
                            handle,
                            buffer.AsMemory(0, written),
                            (long)chunk.Offset,
                            cancellationToken);
                        reportChunkProgress(written, 0);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    await DelayBeforeRetryAsync(attempt, statusCode: null, cancellationToken);
                }
            }

            throw new HttpRequestException(
                $"Failed to download chunk {chunkHex} for {job.File.FileName} after {MaxRetriesPerChunk} attempts.",
                lastError);
        }
        finally
        {
            _chunkGate.Release();
        }
    }

    private static async Task<List<string>> GetCdnServers(int cellId, CancellationToken cancellationToken)
    {
        if (cellId < 0)
            cellId = 0;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Valve/Steam HTTP Client 1.0");

        string url = string.Format(ContentServerDirectoryUrlFormat, cellId);
        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("response", out var responseNode) ||
            !responseNode.TryGetProperty("servers", out var serverNodes))
            throw new InvalidDataException("Steam returned an invalid content-server response.");

        var servers = serverNodes.EnumerateArray()
            .Where(server =>
            {
                string? type = server.TryGetProperty("type", out var value) ? value.GetString() : null;
                return type is "CDN" or "SteamCache";
            })
            .Select(server =>
            {
                string? vhost = server.TryGetProperty("vhost", out var value) ? value.GetString() : null;
                string? host = server.TryGetProperty("host", out value) ? value.GetString() : null;
                return !string.IsNullOrWhiteSpace(vhost) ? vhost : host;
            })
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => host!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (servers.Count == 0)
            throw new InvalidOperationException("Steam returned no usable public content servers.");

        return servers;
    }

    private static async Task DelayBeforeRetryAsync(
        int attempt,
        System.Net.HttpStatusCode? statusCode,
        CancellationToken cancellationToken)
    {
        // 503/429 get a longer backoff; otherwise rotate quickly across CDN hosts.
        int baseMs = statusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests
            ? 1500
            : 250;
        int delayMs = Math.Min(8_000, baseMs * (1 << Math.Min(attempt, 4)));

        try
        {
            await Task.Delay(delayMs, cancellationToken);
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
