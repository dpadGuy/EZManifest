using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using EZManifest.Models;
using Microsoft.Win32.SafeHandles;
using SteamKit2;
using SteamKit2.CDN;

namespace EZManifest.Services;

/// <summary>
/// DepotDownloader-style chunk workers.
/// Pre-sizes files as sparse (when possible) so high-offset writes don't zero-fill and hang.
/// </summary>
public static class GameDownload
{
    private const int MaxRetriesPerChunk = 20;
    private const int ProgressReportMinIntervalMs = 100;
    private const int ProgressHeartbeatIntervalMs = 1000;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ResponseBodyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(2);

    private const uint FSCTL_SET_SPARSE = 0x000900c4;

    private const string ContentServerDirectoryUrlFormat =
        "https://api.steampowered.com/IContentServerDirectoryService/GetServersForSteamPipe/v1/?cell_id={0}&max_servers=80";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    public static async Task BatchEngineStart(
        List<DepotInfo> depots,
        Dictionary<string, byte[]> depotKeys,
        string downloadLocation,
        IProgress<DownloadProgress> progressReporter,
        Func<CancellationToken, Task> waitIfPaused,
        CancellationToken cancellationToken,
        int cdnCellId = 0,
        int maxConcurrentChunks = 16)
    {
        maxConcurrentChunks = Math.Clamp(maxConcurrentChunks, 1, 64);
        int maxConnectionsPerServer = Math.Clamp(maxConcurrentChunks, 1, 32);
        var batchSw = Stopwatch.StartNew();

        AppLog.Write(
            $"[EZManifest] BatchEngineStart begin | depots={depots.Count} keys={depotKeys.Count} " +
            $"cellId={cdnCellId} concurrency={maxConcurrentChunks} maxConn/server={maxConnectionsPerServer}");
        AppLog.Write($"[EZManifest] Install path: {downloadLocation}");
        AppLog.Write(
            $"[EZManifest] Timeouts: headers={RequestTimeout.TotalSeconds}s body={ResponseBodyTimeout.TotalSeconds}s " +
            $"write={WriteTimeout.TotalSeconds}s retries/chunk={MaxRetriesPerChunk}");

        var servers = await GetCdnServers(cdnCellId, cancellationToken);
        var serverPool = new CdnServerPool(servers);
        AppLog.Write($"[EZManifest] CDN pool ready: {servers.Count} host entries (unique={servers.Distinct(StringComparer.OrdinalIgnoreCase).Count()})");

        long totalBytes = 0;
        var workItems = new List<ChunkWork>();
        var fileStates = new List<FileWriteState>();
        int preparedFiles = 0;
        int skippedDepotsNoKey = 0;

        foreach (var depot in depots)
        {
            if (!depotKeys.ContainsKey(depot.DepotId))
            {
                skippedDepotsNoKey++;
                AppLog.Write($"[EZManifest] Depot {depot.DepotId}: skipped (no decryption key)");
                continue;
            }

            byte[] key = depotKeys[depot.DepotId];
            cancellationToken.ThrowIfCancellationRequested();

            var depotSw = Stopwatch.StartNew();
            AppLog.Write($"[EZManifest] Depot {depot.DepotId}: loading manifest {depot.ManifestPath}");

            byte[] manifestData = await File.ReadAllBytesAsync(depot.ManifestPath, cancellationToken);
            AppLog.Write($"[EZManifest] Depot {depot.DepotId}: manifest {AppLog.FormatBytes(manifestData.Length)} on disk");

            var manifest = DepotManifest.Deserialize(manifestData);
            manifest.DecryptFilenames(key);

            int depotFiles = 0;
            int depotChunks = 0;
            long depotBytes = 0;
            int fileIndex = 0;

            foreach (var file in manifest.Files!.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory)))
            {
                if (file.Chunks.Count == 0)
                {
                    AppLog.Write($"[EZManifest] Depot {depot.DepotId}: skip empty file '{file.FileName}'");
                    continue;
                }

                string safePath = Path.Combine(file.FileName.Split('/', '\\'));
                string targetPath = Path.Combine(downloadLocation, safePath);
                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                long fileSize = file.Chunks.Sum(c => (long)c.UncompressedLength);
                if (file.TotalSize > 0)
                    fileSize = Math.Max(fileSize, (long)file.TotalSize);

                fileIndex++;
                if (fileIndex <= 12 || fileSize >= 50L * 1024 * 1024 || fileIndex % 100 == 0)
                {
                    AppLog.Write(
                        $"[EZManifest] Depot {depot.DepotId}: prepare #{fileIndex} '{file.FileName}' " +
                        $"chunks={file.Chunks.Count} size={AppLog.FormatBytes(fileSize)}");
                }

                // DepotDownloader pre-sizes the file. Mark sparse first so SetLength doesn't
                // zero-fill on NTFS (high-offset writes without this hang for minutes on E:).
                await PrepareFileAsync(targetPath, fileSize, cancellationToken);
                preparedFiles++;

                var state = new FileWriteState(targetPath, file.Chunks.Count);
                fileStates.Add(state);

                // Offset order reduces gap-extension even on non-sparse volumes.
                foreach (var chunk in file.Chunks.OrderBy(c => c.Offset))
                {
                    workItems.Add(new ChunkWork
                    {
                        DepotId = depot.DepotId,
                        DepotKey = key,
                        Chunk = chunk,
                        File = state,
                        FileName = file.FileName
                    });
                    totalBytes += chunk.UncompressedLength;
                    depotBytes += chunk.UncompressedLength;
                    depotChunks++;
                }

                depotFiles++;
            }

            AppLog.Write(
                $"[EZManifest] Depot {depot.DepotId}: ready files={depotFiles} chunks={depotChunks} " +
                $"bytes={AppLog.FormatBytes(depotBytes)} in {depotSw.ElapsedMilliseconds}ms");
        }

        if (skippedDepotsNoKey > 0)
            AppLog.Write($"[EZManifest] Skipped {skippedDepotsNoKey} depot(s) without keys");

        AppLog.Write(
            $"[EZManifest] Queue ready: files={preparedFiles} chunks={workItems.Count} " +
            $"bytes={AppLog.FormatBytes(totalBytes)} ({totalBytes}) CDN hosts={servers.Count} " +
            $"prep={batchSw.ElapsedMilliseconds}ms");
        progressReporter.Report(new DownloadProgress(0, totalBytes, 0));
        AppLog.Write($"[EZManifest] Starting {maxConcurrentChunks} chunk worker(s)...");

        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = maxConnectionsPerServer,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = false,
            ConnectTimeout = RequestTimeout,
            AutomaticDecompression = DecompressionMethods.None
        };
        using var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Valve/Steam HTTP Client 1.0");

        long processedBytes = 0;
        long networkBytes = 0;
        long lastProgressReportMs = 0;
        long lastByteProgressMs = Environment.TickCount64;
        long lastMilestoneLogMs = Environment.TickCount64;
        int nextWorkIndex = -1;
        int completedChunks = 0;
        int failedChunks = 0;
        int lastLoggedPercent = -1;
        int activeWorkers = 0;

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

            long done = Interlocked.Read(ref processedBytes);
            long net = Interlocked.Read(ref networkBytes);
            progressReporter.Report(new DownloadProgress(done, totalBytes, net));

            if (totalBytes > 0)
            {
                int pct = (int)(done * 100 / totalBytes);
                int milestone = pct / 5 * 5;
                int previousMilestone = Volatile.Read(ref lastLoggedPercent);
                if (milestone > previousMilestone && milestone > 0 &&
                    Interlocked.CompareExchange(ref lastLoggedPercent, milestone, previousMilestone) == previousMilestone)
                {
                    long elapsed = Math.Max(1, nowMs - Volatile.Read(ref lastMilestoneLogMs));
                    double mbps = (net * 8.0) / (elapsed * 1000.0);
                    AppLog.Write(
                        $"[EZManifest] Progress {milestone}% | {AppLog.FormatBytes(done)}/{AppLog.FormatBytes(totalBytes)} " +
                        $"net={AppLog.FormatBytes(net)} chunks={Volatile.Read(ref completedChunks)}/{workItems.Count} " +
                        $"~{mbps:0.##} Mbps (since last milestone) activeW={Volatile.Read(ref activeWorkers)}");
                    Interlocked.Exchange(ref lastMilestoneLogMs, nowMs);
                }
            }
        }

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = Task.Run(async () =>
        {
            try
            {
                while (!heartbeatCts.IsCancellationRequested)
                {
                    await Task.Delay(ProgressHeartbeatIntervalMs, heartbeatCts.Token);
                    long silentMs = Environment.TickCount64 - Volatile.Read(ref lastByteProgressMs);
                    long done = Interlocked.Read(ref processedBytes);
                    long net = Interlocked.Read(ref networkBytes);
                    int chunksDone = Volatile.Read(ref completedChunks);

                    if (silentMs > 10_000)
                    {
                        AppLog.Write(
                            $"[EZManifest] STALL: no byte progress for {silentMs / 1000}s | " +
                            $"chunks={chunksDone}/{workItems.Count} failed={Volatile.Read(ref failedChunks)} | " +
                            $"bytes={AppLog.FormatBytes(done)}/{AppLog.FormatBytes(totalBytes)} " +
                            $"net={AppLog.FormatBytes(net)} activeW={Volatile.Read(ref activeWorkers)} " +
                            $"bannedCDN={serverPool.BannedCount}");
                    }
                    else if (silentMs > 5_000)
                    {
                        AppLog.Write(
                            $"[EZManifest] Slow: {silentMs / 1000}s since last bytes | " +
                            $"chunks={chunksDone}/{workItems.Count} | {AppLog.FormatBytes(done)}/{AppLog.FormatBytes(totalBytes)}");
                    }

                    ReportProgress(force: true);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, heartbeatCts.Token);

        async Task Worker(int workerId)
        {
            Interlocked.Increment(ref activeWorkers);
            AppLog.Write($"[EZManifest] Worker W{workerId} started");
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int index = Interlocked.Increment(ref nextWorkIndex);
                    if (index >= workItems.Count)
                        return;

                    var work = workItems[index];
                    if (index < 20 || index % 250 == 0)
                    {
                        AppLog.Write(
                            $"[EZManifest] W{workerId} claim #{index}/{workItems.Count} depot={work.DepotId} " +
                            $"file={work.FileName} off={work.Chunk.Offset} " +
                            $"cmp={work.Chunk.CompressedLength} unc={work.Chunk.UncompressedLength}");
                    }

                    await waitIfPaused(cancellationToken);

                    var chunkSw = Stopwatch.StartNew();
                    try
                    {
                        await DownloadChunkAsync(
                            work,
                            httpClient,
                            serverPool,
                            waitIfPaused,
                            (written, received) =>
                            {
                                if (written > 0)
                                {
                                    Interlocked.Add(ref processedBytes, written);
                                    Interlocked.Exchange(ref lastByteProgressMs, Environment.TickCount64);
                                }

                                if (received > 0)
                                {
                                    Interlocked.Add(ref networkBytes, received);
                                    Interlocked.Exchange(ref lastByteProgressMs, Environment.TickCount64);
                                }

                                ReportProgress();
                            },
                            cancellationToken);

                        int done = Interlocked.Increment(ref completedChunks);
                        if (chunkSw.ElapsedMilliseconds >= 3_000 || index < 5 || done % 500 == 0)
                        {
                            AppLog.Write(
                                $"[EZManifest] W{workerId} OK #{index} in {chunkSw.ElapsedMilliseconds}ms " +
                                $"file={work.FileName} done={done}/{workItems.Count}");
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        AppLog.Write($"[EZManifest] W{workerId} cancelled on chunk #{index} file={work.FileName}");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failedChunks);
                        AppLog.Write(
                            ex,
                            $"W{workerId} FAILED #{index} depot={work.DepotId} file={work.FileName} " +
                            $"off={work.Chunk.Offset} after {chunkSw.ElapsedMilliseconds}ms");
                        throw;
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref activeWorkers);
                AppLog.Write($"[EZManifest] Worker W{workerId} exited");
            }
        }

        try
        {
            var workers = Enumerable.Range(0, maxConcurrentChunks).Select(Worker).ToArray();
            await Task.WhenAll(workers);
            ReportProgress(force: true);
            cancellationToken.ThrowIfCancellationRequested();
            AppLog.Write(
                $"[EZManifest] Download finished in {batchSw.Elapsed} | " +
                $"chunks={completedChunks}/{workItems.Count} bytes={AppLog.FormatBytes(processedBytes)} " +
                $"net={AppLog.FormatBytes(networkBytes)} failed={failedChunks}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppLog.Write(
                $"[EZManifest] Download cancelled after {batchSw.Elapsed} | " +
                $"chunks={Volatile.Read(ref completedChunks)}/{workItems.Count} " +
                $"bytes={AppLog.FormatBytes(Interlocked.Read(ref processedBytes))}/{AppLog.FormatBytes(totalBytes)}");
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Write(
                ex,
                $"[EZManifest] Download aborted after {batchSw.Elapsed} | " +
                $"chunks={Volatile.Read(ref completedChunks)}/{workItems.Count} " +
                $"bytes={AppLog.FormatBytes(Interlocked.Read(ref processedBytes))}/{AppLog.FormatBytes(totalBytes)}");
            throw;
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat; } catch { /* ignore */ }

            AppLog.Write($"[EZManifest] Disposing {fileStates.Count} file write state(s)...");
            foreach (var state in fileStates)
                state.Dispose();
            AppLog.Write($"[EZManifest] BatchEngineStart cleanup done ({batchSw.Elapsed})");
        }
    }

    private static async Task PrepareFileAsync(string path, long fileSize, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        bool sparseOk = false;
        await Task.Run(() =>
        {
            using var fs = new FileStream(
                path,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.None);

            // Sparse makes SetLength metadata-only on NTFS (no zero-fill).
            sparseOk = TryMarkSparse(fs.SafeFileHandle);

            if (fileSize > 0)
                fs.SetLength(fileSize);
        }, cancellationToken);

        if (sw.ElapsedMilliseconds > 200 || fileSize >= 100L * 1024 * 1024 || !sparseOk)
        {
            AppLog.Write(
                $"[EZManifest] PrepareFile {sw.ElapsedMilliseconds}ms sparse={sparseOk} " +
                $"size={AppLog.FormatBytes(fileSize)} → {path}");
        }
    }

    private static bool TryMarkSparse(SafeFileHandle handle)
    {
        try
        {
            bool ok = DeviceIoControl(
                handle,
                FSCTL_SET_SPARSE,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);
            if (!ok)
                AppLog.Write($"[EZManifest] FSCTL_SET_SPARSE failed win32={Marshal.GetLastWin32Error()}");
            return ok;
        }
        catch (Exception ex)
        {
            // Non-NTFS volumes may not support sparse files.
            AppLog.Write($"[EZManifest] Sparse mark threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static async Task DownloadChunkAsync(
        ChunkWork work,
        HttpClient client,
        CdnServerPool serverPool,
        Func<CancellationToken, Task> waitIfPaused,
        Action<int, int> reportProgress,
        CancellationToken cancellationToken)
    {
        byte[]? chunkId = work.Chunk.ChunkID
            ?? throw new InvalidDataException($"Chunk ID missing for {work.FileName}.");
        string chunkHex = Convert.ToHexString(chunkId).ToLowerInvariant();

        Exception? lastError = null;

        for (int attempt = 0; attempt < MaxRetriesPerChunk; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await waitIfPaused(cancellationToken);

            string server = serverPool.GetConnection();
            string url = $"http://{server}/depot/{work.DepotId}/chunk/{chunkHex}";
            var attemptSw = Stopwatch.StartNew();

            using var timeoutCts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            timeoutCts.CancelAfter(RequestTimeout);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };

                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    linkedCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    bool hardBan = response.StatusCode is HttpStatusCode.Forbidden
                        or HttpStatusCode.Unauthorized
                        or HttpStatusCode.NotFound;
                    lastError = new HttpRequestException(
                        $"CDN {server} returned {(int)response.StatusCode} {response.StatusCode} for chunk {chunkHex}.");
                    serverPool.ReturnBrokenConnection(server, hard: hardBan);
                    AppLog.Write(
                        $"[EZManifest] HTTP {(int)response.StatusCode} host={server} depot={work.DepotId} " +
                        $"chunk={chunkHex[..Math.Min(12, chunkHex.Length)]}… attempt={attempt + 1}/{MaxRetriesPerChunk} " +
                        $"hardBan={hardBan} banned={serverPool.BannedCount} file={work.FileName}");
                    await DelayBeforeRetryAsync(attempt, response.StatusCode, cancellationToken);
                    continue;
                }

                timeoutCts.CancelAfter(ResponseBodyTimeout);

                var downloadSw = Stopwatch.StartNew();
                byte[] encrypted = await response.Content.ReadAsByteArrayAsync(linkedCts.Token);
                long downloadMs = downloadSw.ElapsedMilliseconds;
                reportProgress(0, encrypted.Length);

                byte[] buffer = ArrayPool<byte>.Shared.Rent((int)work.Chunk.UncompressedLength + 65536);
                try
                {
                    var processSw = Stopwatch.StartNew();
                    int written = DepotChunk.Process(work.Chunk, encrypted, buffer, work.DepotKey);
                    long processMs = processSw.ElapsedMilliseconds;

                    var writeSw = Stopwatch.StartNew();
                    await work.File.WriteAsync(buffer.AsMemory(0, written), (long)work.Chunk.Offset, cancellationToken);
                    long writeMs = writeSw.ElapsedMilliseconds;
                    reportProgress(written, 0);

                    if (attempt > 0 || downloadMs >= 2_000 || processMs >= 500 || writeMs >= 1_000 ||
                        attemptSw.ElapsedMilliseconds >= 3_000)
                    {
                        AppLog.Write(
                            $"[EZManifest] Chunk OK host={server} attempt={attempt + 1} " +
                            $"dl={downloadMs}ms proc={processMs}ms write={writeMs}ms total={attemptSw.ElapsedMilliseconds}ms " +
                            $"net={AppLog.FormatBytes(encrypted.Length)} unc={AppLog.FormatBytes(written)} " +
                            $"off={work.Chunk.Offset} file={work.FileName}");
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                serverPool.ReturnConnection(server);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AppLog.Write(
                    $"[EZManifest] Chunk cancelled host={server} chunk={chunkHex[..Math.Min(12, chunkHex.Length)]}… " +
                    $"file={work.FileName} after {attemptSw.ElapsedMilliseconds}ms");
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                serverPool.ReturnBrokenConnection(server, hard: false);
                AppLog.Write(
                    $"[EZManifest] Chunk fail host={server} attempt={attempt + 1}/{MaxRetriesPerChunk} " +
                    $"after {attemptSw.ElapsedMilliseconds}ms chunk={chunkHex[..Math.Min(12, chunkHex.Length)]}… " +
                    $"file={work.FileName}: {ex.GetType().Name}: {ex.Message}");
                await DelayBeforeRetryAsync(attempt, statusCode: null, cancellationToken);
            }
        }

        AppLog.Write(
            $"[EZManifest] Chunk exhausted retries chunk={chunkHex} file={work.FileName} depot={work.DepotId} " +
            $"last={lastError?.GetType().Name}: {lastError?.Message}");
        throw new HttpRequestException(
            $"Failed to download chunk {chunkHex} for {work.FileName} after {MaxRetriesPerChunk} attempts.",
            lastError);
    }

    private static async Task DelayBeforeRetryAsync(
        int attempt,
        HttpStatusCode? statusCode,
        CancellationToken cancellationToken)
    {
        int baseMs = statusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests
            ? 1500
            : 300;
        int delayMs = Math.Min(5_000, baseMs * (1 << Math.Min(attempt, 3)));
        AppLog.Write(
            $"[EZManifest] Retry backoff {delayMs}ms (attempt={attempt + 1} status={statusCode?.ToString() ?? "n/a"})");
        await Task.Delay(delayMs, cancellationToken);
    }

    private static async Task<List<string>> GetCdnServers(int cellId, CancellationToken cancellationToken)
    {
        if (cellId < 0)
            cellId = 0;

        string url = string.Format(ContentServerDirectoryUrlFormat, cellId);
        AppLog.Write($"[EZManifest] Fetching CDN directory cellId={cellId} url={url}");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Valve/Steam HTTP Client 1.0");

        var sw = Stopwatch.StartNew();
        using var response = await client.GetAsync(url, cancellationToken);
        AppLog.Write(
            $"[EZManifest] CDN directory HTTP {(int)response.StatusCode} in {sw.ElapsedMilliseconds}ms");
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("response", out var responseNode) ||
            !responseNode.TryGetProperty("servers", out var serverNodes))
            throw new InvalidDataException("Steam returned an invalid content-server response.");

        int rawCount = 0;
        int accepted = 0;
        var servers = serverNodes.EnumerateArray()
            .SelectMany(server =>
            {
                rawCount++;
                string? type = server.TryGetProperty("type", out var value) ? value.GetString() : null;
                if (type is not ("CDN" or "SteamCache"))
                    return Array.Empty<string>();

                accepted++;
                string? vhost = server.TryGetProperty("vhost", out value) ? value.GetString() : null;
                string? host = server.TryGetProperty("host", out value) ? value.GetString() : null;
                string endpoint = !string.IsNullOrWhiteSpace(vhost) ? vhost! : host ?? string.Empty;
                if (string.IsNullOrWhiteSpace(endpoint))
                    return Array.Empty<string>();

                int entries = 1;
                if (server.TryGetProperty("num_entries", out var entriesValue) &&
                    entriesValue.TryGetInt32(out var n) &&
                    n > 0)
                {
                    entries = Math.Min(n, 5);
                }

                return Enumerable.Repeat(endpoint, entries);
            })
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .ToList();

        if (servers.Count == 0)
            throw new InvalidOperationException("Steam returned no usable public content servers.");

        var unique = servers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        AppLog.Write(
            $"[EZManifest] CDN directory parsed: raw={rawCount} acceptedType={accepted} " +
            $"weighted={servers.Count} unique={unique.Count} in {sw.ElapsedMilliseconds}ms");
        for (int i = 0; i < Math.Min(unique.Count, 12); i++)
            AppLog.Write($"[EZManifest]   CDN[{i}] {unique[i]}");
        if (unique.Count > 12)
            AppLog.Write($"[EZManifest]   …and {unique.Count - 12} more unique host(s)");

        return servers;
    }

    private sealed class ChunkWork
    {
        public required string DepotId { get; init; }
        public required byte[] DepotKey { get; init; }
        public required DepotManifest.ChunkData Chunk { get; init; }
        public required FileWriteState File { get; init; }
        public required string FileName { get; init; }
    }

    private sealed class CdnServerPool
    {
        private readonly List<string> _servers;
        private readonly ConcurrentDictionary<string, byte> _banned = new(StringComparer.OrdinalIgnoreCase);
        private int _next;

        public CdnServerPool(List<string> servers) => _servers = servers;

        public int BannedCount => _banned.Count;

        public string GetConnection()
        {
            for (int i = 0; i < _servers.Count; i++)
            {
                int index = Math.Abs(Interlocked.Increment(ref _next)) % _servers.Count;
                string host = _servers[index];
                if (!_banned.ContainsKey(host))
                    return host;
            }

            AppLog.Write($"[EZManifest] All CDN hosts banned ({_banned.Count}); clearing ban list and retrying");
            _banned.Clear();
            return _servers[Math.Abs(Interlocked.Increment(ref _next)) % _servers.Count];
        }

        public void ReturnConnection(string _)
        {
        }

        public void ReturnBrokenConnection(string server, bool hard)
        {
            Interlocked.Increment(ref _next);
            if (hard)
            {
                if (_banned.TryAdd(server, 1))
                    AppLog.Write($"[EZManifest] CDN hard-banned: {server} (banned={_banned.Count}/{_servers.Distinct(StringComparer.OrdinalIgnoreCase).Count()})");
            }
        }
    }

    private sealed class FileWriteState : IDisposable
    {
        private readonly string _path;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private FileStream? _stream;
        private int _remainingChunks;
        private int _opened;

        public FileWriteState(string path, int chunkCount)
        {
            _path = path;
            _remainingChunks = chunkCount;
        }

        public async Task WriteAsync(ReadOnlyMemory<byte> data, long offset, CancellationToken cancellationToken)
        {
            var waitSw = Stopwatch.StartNew();
            await _writeLock.WaitAsync(cancellationToken);
            long lockWaitMs = waitSw.ElapsedMilliseconds;
            try
            {
                if (_stream is null)
                {
                    _stream = new FileStream(
                        _path,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        bufferSize: 256 * 1024,
                        FileOptions.Asynchronous | FileOptions.RandomAccess);
                    if (Interlocked.Exchange(ref _opened, 1) == 0)
                        AppLog.Write($"[EZManifest] Opened write stream {_path} remainingChunks={_remainingChunks}");
                }

                _stream.Position = offset;

                using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                writeCts.CancelAfter(WriteTimeout);
                var writeSw = Stopwatch.StartNew();
                try
                {
                    await _stream.WriteAsync(data, writeCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    AppLog.Write(
                        $"[EZManifest] Disk WRITE TIMEOUT after {WriteTimeout.TotalSeconds}s " +
                        $"off={offset} len={data.Length} lockWait={lockWaitMs}ms path={_path}");
                    throw new TimeoutException(
                        $"Disk write timed out after {WriteTimeout.TotalSeconds}s at offset {offset} in {_path}");
                }

                long writeMs = writeSw.ElapsedMilliseconds;
                if (lockWaitMs >= 1_000 || writeMs >= 1_000)
                {
                    AppLog.Write(
                        $"[EZManifest] Slow disk write lockWait={lockWaitMs}ms write={writeMs}ms " +
                        $"off={offset} len={AppLog.FormatBytes(data.Length)} path={_path}");
                }

                if (Interlocked.Decrement(ref _remainingChunks) == 0)
                {
                    AppLog.Write($"[EZManifest] File complete, closing stream {_path}");
                    await _stream.DisposeAsync();
                    _stream = null;
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            if (!_writeLock.Wait(5000))
            {
                AppLog.Write($"[EZManifest] Timed out disposing file state remaining={_remainingChunks} path={_path}");
                return;
            }

            try
            {
                if (_stream is not null)
                    AppLog.Write($"[EZManifest] Dispose still-open stream remaining={_remainingChunks} path={_path}");
                _stream?.Dispose();
                _stream = null;
            }
            finally
            {
                _writeLock.Release();
                _writeLock.Dispose();
            }
        }
    }
}
