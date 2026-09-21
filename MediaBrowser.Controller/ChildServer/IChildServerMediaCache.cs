using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Controller.ChildServer;

/// <summary>
/// Keeps the media files mirrored from the parent server: knows which local paths are placeholders,
/// downloads the real file when it is needed and serves it while it is still arriving.
/// </summary>
public interface IChildServerMediaCache
{
    /// <summary>
    /// Gets a value indicating whether the path belongs to a file mirrored from the parent server.
    /// </summary>
    /// <param name="path">The local path of an item.</param>
    /// <returns>True when the file is managed by the child server cache.</returns>
    bool IsManagedPath(string? path);

    /// <summary>
    /// Gets the cache state of a local path.
    /// </summary>
    /// <param name="path">The local path of an item.</param>
    /// <returns>The state; <see cref="ChildMediaCacheState.Unmanaged"/> for paths that are not mirrored.</returns>
    ChildMediaCacheState GetState(string? path);

    /// <summary>
    /// Gets what the parent server knows about a mirrored file, so the placeholder does not have to be probed.
    /// </summary>
    /// <param name="path">The local path of an item.</param>
    /// <returns>The media information, or null for paths that are not mirrored.</returns>
    MirroredMediaInfo? GetMediaInfo(string? path);

    /// <summary>
    /// Downloads a mirrored file from the parent unless it is already stored on this device.
    /// </summary>
    /// <param name="path">The local path of the item.</param>
    /// <param name="priority">How urgently the file is needed.</param>
    /// <param name="cancellationToken">Cancels the wait, not the download.</param>
    /// <returns>A task that completes when the file is fully stored.</returns>
    /// <exception cref="ParentServerUnavailableException">The parent could not be reached.</exception>
    Task EnsureCachedAsync(string path, ChildFetchPriority priority, CancellationToken cancellationToken);

    /// <summary>
    /// Called before a stream of the item is opened. Starts the download; for anything that is not a
    /// static file stream it also waits until the file is complete, because the encoder needs the whole file.
    /// </summary>
    /// <param name="item">The item about to be streamed.</param>
    /// <param name="mediaSource">The media source about to be streamed.</param>
    /// <param name="isStaticStream">True when the file is served as is, without transcoding.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when streaming may start.</returns>
    /// <exception cref="ParentServerUnavailableException">The parent could not be reached and the file is not cached.</exception>
    Task PrepareForStreamingAsync(BaseItem item, MediaSourceInfo mediaSource, bool isStaticStream, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a seekable read stream over a file that is still being downloaded. Reads wait for the bytes to arrive.
    /// </summary>
    /// <param name="path">The local path of the item.</param>
    /// <returns>The stream, or null when the file is complete or not mirrored, in which case the file can be served directly.</returns>
    Stream? OpenProgressiveStream(string? path);

    /// <summary>
    /// Checks whether the parent server answers. The answer is cached for a few seconds.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when the parent answered.</returns>
    Task<bool> IsParentReachableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets totals about the mirrored files on this device.
    /// </summary>
    /// <returns>The statistics.</returns>
    ChildCacheStatistics GetStatistics();
}
