using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.ChildServer.Mirror;
using MediaBrowser.Controller.ChildServer;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.ChildServer;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.ChildServer.Cache;

/// <summary>
/// Downloads mirrored media files from the parent server one at a time, serves them while they arrive,
/// and evicts the least recently used files when the cache grows past its limit.
/// </summary>
public sealed class ChildMediaCache : IChildServerMediaCache, IDisposable
{
    private const int ChunkSize = 256 * 1024;
    private const int MaxAttempts = 3;
    private const string PartSuffix = ".jfpart";

    private static readonly TimeSpan _reachabilityCacheTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _reachabilityTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan _evictionGrace = TimeSpan.FromMinutes(2);

    private readonly ChildServerManager _manager;
    private readonly ParentServerClient _client;
    private readonly ParentLibraryReader _reader;
    private readonly MirrorManifest _manifest;
    private readonly ILogger<ChildMediaCache> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DownloadJob> _jobs = new(MirrorManifest.PathComparer);
    private readonly PriorityQueue<DownloadJob, (int Priority, long Sequence)> _queue = new();
    private readonly ConcurrentDictionary<string, int> _readers = new(MirrorManifest.PathComparer);
    private readonly SemaphoreSlim _reachabilityLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private long _sequence;
    private Task? _worker;
    private DateTime _reachabilityCheckedUtc = DateTime.MinValue;
    private bool _reachable;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildMediaCache"/> class.
    /// </summary>
    /// <param name="manager">The child server manager.</param>
    /// <param name="client">The parent server client, for reachability checks.</param>
    /// <param name="reader">The parent library reader, for media downloads.</param>
    /// <param name="manifest">The mirror manifest.</param>
    /// <param name="logger">The logger.</param>
    public ChildMediaCache(ChildServerManager manager, ParentServerClient client, ParentLibraryReader reader, MirrorManifest manifest, ILogger<ChildMediaCache> logger)
    {
        _manager = manager;
        _client = client;
        _reader = reader;
        _manifest = manifest;
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets the pause before a failed download is retried; multiplied by the attempt number.
    /// </summary>
    internal TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets how many downloads are queued or running.
    /// </summary>
    public int ActiveDownloads
    {
        get
        {
            lock (_lock)
            {
                return _jobs.Values.Count(j => !j.IsFinished);
            }
        }
    }

    /// <inheritdoc />
    public bool IsManagedPath(string? path) => _manifest.Get(path) is not null;

    /// <inheritdoc />
    public ChildMediaCacheState GetState(string? path)
    {
        var entry = _manifest.Get(path);
        if (entry is null)
        {
            return ChildMediaCacheState.Unmanaged;
        }

        // The disk is the truth: a job may still be finishing its bookkeeping after the file landed.
        var cachedBytes = GetCachedBytes(entry);
        if (cachedBytes.HasValue)
        {
            return new ChildMediaCacheState(true, true, false, cachedBytes.Value, entry.ExpectedSize);
        }

        DownloadJob? job;
        lock (_lock)
        {
            _jobs.TryGetValue(entry.Path, out job);
        }

        if (job is not null && !job.IsFinished)
        {
            var expected = job.ExpectedBytes > 0 ? job.ExpectedBytes : entry.ExpectedSize;
            return new ChildMediaCacheState(true, false, true, job.DownloadedBytes, expected);
        }

        var partInfo = new FileInfo(GetPartPath(entry));
        return new ChildMediaCacheState(true, false, false, partInfo.Exists ? partInfo.Length : 0, entry.ExpectedSize);
    }

    /// <inheritdoc />
    public MirroredMediaInfo? GetMediaInfo(string? path)
    {
        var entry = _manifest.Get(path);
        if (entry is null)
        {
            return null;
        }

        return new MirroredMediaInfo
        {
            ParentItemId = entry.ParentItemId,
            ParentMediaSourceId = entry.ParentMediaSourceId,
            Container = entry.Container,
            RunTimeTicks = entry.RunTimeTicks,
            Bitrate = entry.Bitrate,
            Size = entry.ExpectedSize,
            MediaStreams = entry.MediaStreams
        };
    }

    /// <inheritdoc />
    public async Task EnsureCachedAsync(string path, ChildFetchPriority priority, CancellationToken cancellationToken)
    {
        var entry = _manifest.Get(path);
        if (entry is null)
        {
            return;
        }

        if (GetCachedBytes(entry).HasValue)
        {
            Touch(entry);
            return;
        }

        var job = GetOrStartJob(entry, priority);
        await job.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PrepareForStreamingAsync(BaseItem item, MediaSourceInfo mediaSource, bool isStaticStream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mediaSource);

        var entry = _manifest.Get(mediaSource.Path) ?? _manifest.Get(item?.Path);
        if (entry is null)
        {
            return;
        }

        if (GetCachedBytes(entry).HasValue)
        {
            Touch(entry);
            return;
        }

        var job = GetOrStartJob(entry, ChildFetchPriority.Playback);
        if (isStaticStream)
        {
            // The static stream endpoint serves the file while it downloads.
            return;
        }

        var timeoutSeconds = _manager.Configuration.DownloadWaitTimeoutSeconds;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutSeconds > 0)
        {
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        }

        try
        {
            await job.Completion.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ParentServerUnavailableException(
                "The download from the parent server did not finish within " + timeoutSeconds.ToString(CultureInfo.InvariantCulture) + " seconds.");
        }
    }

    /// <inheritdoc />
    public Stream? OpenProgressiveStream(string? path)
    {
        var entry = _manifest.Get(path);
        if (entry is null || GetCachedBytes(entry).HasValue)
        {
            return null;
        }

        var job = GetOrStartJob(entry, ChildFetchPriority.Playback);
        _readers.AddOrUpdate(entry.Path, 1, (_, count) => count + 1);
        return new ChildCacheStream(job, () => _readers.AddOrUpdate(entry.Path, 0, (_, count) => Math.Max(0, count - 1)));
    }

    /// <inheritdoc />
    public async Task<bool> IsParentReachableAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _reachabilityCheckedUtc < _reachabilityCacheTime)
        {
            return _reachable;
        }

        await _reachabilityLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow - _reachabilityCheckedUtc < _reachabilityCacheTime)
            {
                return _reachable;
            }

            var config = _manager.Configuration;
            var baseUrl = ParentServerClient.TryParseBaseUrl(config.ParentUrl);
            var reachable = false;
            if (baseUrl is not null)
            {
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(_reachabilityTimeout);
                try
                {
                    await _client.GetPublicSystemInfoAsync(new ParentEndpoint(baseUrl, config.CustomHeaders), timeoutSource.Token).ConfigureAwait(false);
                    reachable = true;
                }
                catch (ParentServerException ex)
                {
                    _logger.LogDebug("Parent server reachability check failed: {Message}", ex.Message);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug("Parent server reachability check timed out");
                }
            }

            _reachable = reachable;
            _reachabilityCheckedUtc = DateTime.UtcNow;
            return reachable;
        }
        finally
        {
            _reachabilityLock.Release();
        }
    }

    /// <inheritdoc />
    public ChildCacheStatistics GetStatistics()
    {
        var entries = _manifest.GetAll();
        var cachedCount = 0;
        long cachedBytes = 0;
        foreach (var entry in entries)
        {
            var size = GetCachedBytes(entry);
            if (size.HasValue)
            {
                cachedCount++;
                cachedBytes += size.Value;
            }
        }

        return new ChildCacheStatistics(entries.Count, cachedCount, cachedBytes, ActiveDownloads);
    }

    /// <summary>
    /// Forgets the last reachability answer so the next check asks the parent again.
    /// </summary>
    public void ResetReachability() => _reachabilityCheckedUtc = DateTime.MinValue;

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _reachabilityLock.Dispose();
    }

    /// <summary>
    /// Gets the size of the cached file when it is fully present on disk.
    /// </summary>
    /// <param name="entry">The manifest entry.</param>
    /// <returns>The size, or null when the file is a placeholder or incomplete.</returns>
    internal static long? GetCachedBytes(MirrorEntry entry)
    {
        var info = new FileInfo(entry.Path);
        if (!info.Exists || info.Length == 0)
        {
            return null;
        }

        if (entry.ExpectedSize > 0 && info.Length != entry.ExpectedSize)
        {
            return null;
        }

        return info.Length;
    }

    private static string GetPartPath(MirrorEntry entry) => entry.Path + PartSuffix;

    private void Touch(MirrorEntry entry)
    {
        entry.LastAccessUtc = DateTime.UtcNow;
        entry.IsCached = true;
    }

    private DownloadJob GetOrStartJob(MirrorEntry entry, ChildFetchPriority priority)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(entry.Path, out var existing) && !existing.IsFinished)
            {
                if (priority < existing.Priority)
                {
                    existing.Priority = priority;
                    _queue.Enqueue(existing, ((int)priority, Interlocked.Increment(ref _sequence)));
                }

                return existing;
            }

            var job = new DownloadJob(entry, GetPartPath(entry), priority);
            _jobs[entry.Path] = job;
            _queue.Enqueue(job, ((int)priority, Interlocked.Increment(ref _sequence)));
            _logger.LogInformation("Queued download of {Name} from the parent server ({Priority})", entry.Name, priority);

            if (_worker is null || _worker.IsCompleted)
            {
                _worker = Task.Run(() => RunWorkerAsync(_shutdown.Token));
            }

            return job;
        }
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        var started = new HashSet<DownloadJob>();
        while (!cancellationToken.IsCancellationRequested)
        {
            DownloadJob job;
            lock (_lock)
            {
                if (!_queue.TryDequeue(out var next, out _))
                {
                    _worker = null;
                    return;
                }

                job = next;
            }

            if (job.IsFinished || !started.Add(job))
            {
                continue;
            }

            await RunJobAsync(job, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunJobAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    var size = await DownloadAsync(job, cancellationToken).ConfigureAwait(false);
                    job.Entry.ExpectedSize = size;
                    Touch(job.Entry);
                    _manifest.Save();
                    _logger.LogInformation("Cached {Name} ({Size} bytes) from the parent server", job.Entry.Name, size);
                    Evict();

                    // Signal last, so whoever waited sees the manifest and the cache limit already applied.
                    job.Complete(size);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    job.Fail(new ParentServerUnavailableException("The server is shutting down."));
                    return;
                }
                catch (ParentServerException ex) when (ex.Status == ParentConnectionStatus.InvalidCredentials && attempt < MaxAttempts)
                {
                    _logger.LogWarning("The parent rejected the stored sign in while downloading {Name}; signing in again", job.Entry.Name);
                    _manager.InvalidateSession();
                }
                catch (Exception ex) when (ex is ParentServerException or ParentServerUnavailableException or IOException or UnauthorizedAccessException)
                {
                    if (attempt >= MaxAttempts)
                    {
                        _logger.LogError(ex, "Downloading {Name} from the parent server failed after {Attempts} attempts", job.Entry.Name, attempt);
                        job.Fail(ex is ParentServerUnavailableException ? ex : new ParentServerUnavailableException("Can't connect to parent server: " + ex.Message, ex));
                        return;
                    }

                    _logger.LogWarning(ex, "Downloading {Name} from the parent server failed (attempt {Attempt}); retrying", job.Entry.Name, attempt);
                    await Task.Delay(RetryBaseDelay * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure while downloading {Name}", job.Entry.Name);
            job.Fail(new ParentServerUnavailableException("Can't connect to parent server: " + ex.Message, ex));
        }
        finally
        {
            lock (_lock)
            {
                if (_jobs.TryGetValue(job.Path, out var current) && ReferenceEquals(current, job))
                {
                    _jobs.Remove(job.Path);
                }
            }
        }
    }

    private async Task<long> DownloadAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        var entry = job.Entry;
        var session = await _manager.GetSessionAsync(cancellationToken).ConfigureAwait(false);

        var partPath = job.PartPath;
        long offset = 0;
        var partInfo = new FileInfo(partPath);
        if (partInfo.Exists && partInfo.Length > 0 && (entry.ExpectedSize == 0 || partInfo.Length < entry.ExpectedSize))
        {
            offset = partInfo.Length;
        }

        using var response = await _reader.OpenMediaAsync(session, entry.ParentItemId, entry.ParentMediaSourceId, offset, cancellationToken).ConfigureAwait(false);
        if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            _logger.LogInformation("The parent server does not resume downloads; restarting {Name} from the beginning", entry.Name);
            offset = 0;
        }

        long? total = response.Content.Headers.ContentLength.HasValue ? offset + response.Content.Headers.ContentLength.Value : null;
        if (total.HasValue && entry.ExpectedSize > 0 && total.Value != entry.ExpectedSize)
        {
            _logger.LogWarning("The parent reports {Reported} bytes for {Name} but the mirror expected {Expected}; using the parent's size", total.Value, entry.Name, entry.ExpectedSize);
            entry.ExpectedSize = total.Value;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
        var file = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        await using (file.ConfigureAwait(false))
        {
            file.SetLength(offset);
            file.Seek(offset, SeekOrigin.Begin);
            job.ReportProgress(offset, total);

            var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
                try
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                        offset += read;
                        job.ReportProgress(offset, total);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        if (entry.ExpectedSize > 0 && offset != entry.ExpectedSize)
        {
            File.Delete(partPath);
            throw new IOException(
                "Expected " + entry.ExpectedSize.ToString(CultureInfo.InvariantCulture) + " bytes for " + entry.Name
                + " but received " + offset.ToString(CultureInfo.InvariantCulture) + ".");
        }

        File.Move(partPath, entry.Path, overwrite: true);
        return offset;
    }

    private void Evict()
    {
        var limitBytes = (long)_manager.Configuration.MaxCacheSizeMb * 1024 * 1024;
        if (limitBytes <= 0)
        {
            return;
        }

        var cached = new List<(MirrorEntry Entry, long Size)>();
        foreach (var entry in _manifest.GetAll())
        {
            var size = GetCachedBytes(entry);
            if (size.HasValue)
            {
                cached.Add((entry, size.Value));
            }
        }

        var total = cached.Sum(c => c.Size);
        if (total <= limitBytes)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var evicted = 0;
        foreach (var (entry, size) in cached.OrderBy(c => c.Entry.LastAccessUtc ?? DateTime.MinValue))
        {
            if (total <= limitBytes)
            {
                break;
            }

            if (_readers.TryGetValue(entry.Path, out var readers) && readers > 0)
            {
                continue;
            }

            lock (_lock)
            {
                if (_jobs.ContainsKey(entry.Path))
                {
                    continue;
                }
            }

            if (entry.LastAccessUtc.HasValue && now - entry.LastAccessUtc.Value < _evictionGrace)
            {
                continue;
            }

            try
            {
                using (var stream = new FileStream(entry.Path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    stream.SetLength(0);
                }

                entry.IsCached = false;
                total -= size;
                evicted++;
                _logger.LogInformation("Evicted {Name} ({Size} bytes) from the media cache", entry.Name, size);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not evict {Name} from the media cache", entry.Name);
            }
        }

        if (evicted > 0)
        {
            _manifest.Save();
        }
    }
}
