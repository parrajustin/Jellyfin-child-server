using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.ChildServer.Mirror;
using MediaBrowser.Controller.ChildServer;

namespace Jellyfin.ChildServer.Cache;

/// <summary>
/// One download of a mirrored file from the parent server. Readers can wait for progress on it.
/// </summary>
public sealed class DownloadJob
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _progress = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _downloadedBytes;
    private long _expectedBytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadJob"/> class.
    /// </summary>
    /// <param name="entry">The manifest entry being downloaded.</param>
    /// <param name="partPath">The temporary file that receives the bytes.</param>
    /// <param name="priority">How urgently the file is needed.</param>
    public DownloadJob(MirrorEntry entry, string partPath, ChildFetchPriority priority)
    {
        Entry = entry;
        PartPath = partPath;
        Priority = priority;
        _expectedBytes = entry.ExpectedSize;
    }

    /// <summary>
    /// Gets the manifest entry.
    /// </summary>
    public MirrorEntry Entry { get; }

    /// <summary>
    /// Gets the local path of the media file.
    /// </summary>
    public string Path => Entry.Path;

    /// <summary>
    /// Gets the temporary file that receives the bytes.
    /// </summary>
    public string PartPath { get; }

    /// <summary>
    /// Gets or sets how urgently the file is needed. Raised when a playback request joins a prefetch.
    /// </summary>
    public ChildFetchPriority Priority { get; set; }

    /// <summary>
    /// Gets how many bytes have been written so far.
    /// </summary>
    public long DownloadedBytes => Volatile.Read(ref _downloadedBytes);

    /// <summary>
    /// Gets the expected size, once known.
    /// </summary>
    public long ExpectedBytes => Volatile.Read(ref _expectedBytes);

    /// <summary>
    /// Gets a value indicating whether the file is complete and in its final place.
    /// </summary>
    public bool IsCompleted { get; private set; }

    /// <summary>
    /// Gets the failure, when the download failed.
    /// </summary>
    public Exception? Error { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the job finished, successfully or not.
    /// </summary>
    public bool IsFinished => IsCompleted || Error is not null;

    /// <summary>
    /// Gets a task that completes when the file is fully stored, or faults when the download failed.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>
    /// Records that more bytes arrived.
    /// </summary>
    /// <param name="totalBytes">The total number of bytes written so far.</param>
    /// <param name="expectedBytes">The expected size, when the parent reported it.</param>
    public void ReportProgress(long totalBytes, long? expectedBytes = null)
    {
        Volatile.Write(ref _downloadedBytes, totalBytes);
        if (expectedBytes is > 0)
        {
            Volatile.Write(ref _expectedBytes, expectedBytes.Value);
        }

        var previous = Interlocked.Exchange(ref _progress, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult();
    }

    /// <summary>
    /// Marks the download as complete. Waiters wake up and future waits return at once.
    /// </summary>
    /// <param name="totalBytes">The final size.</param>
    public void Complete(long totalBytes)
    {
        Volatile.Write(ref _downloadedBytes, totalBytes);
        Volatile.Write(ref _expectedBytes, totalBytes);
        IsCompleted = true;
        Volatile.Read(ref _progress).TrySetResult();
        _completion.TrySetResult();
    }

    /// <summary>
    /// Marks the download as failed. Waiters wake up and future waits return at once.
    /// </summary>
    /// <param name="error">The failure.</param>
    public void Fail(Exception error)
    {
        Error = error;
        Volatile.Read(ref _progress).TrySetResult();
        _completion.TrySetException(error);
    }

    /// <summary>
    /// Waits until more bytes arrive, the job finishes, the timeout passes or the token is cancelled.
    /// </summary>
    /// <param name="timeout">How long to wait at most.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when progress or completion happened, false on timeout.</returns>
    public async Task<bool> WaitForProgressAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (IsFinished)
        {
            return true;
        }

        var task = Volatile.Read(ref _progress).Task;
        try
        {
            await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
