using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.ChildServer;

namespace MediaBrowser.Controller.ChildServer;

/// <summary>
/// Mirrors the parent server's libraries into this server as placeholder files with local metadata.
/// </summary>
public interface IChildServerLibrarySync
{
    /// <summary>
    /// Gets whether a sync is running.
    /// </summary>
    ChildSyncState State { get; }

    /// <summary>
    /// Gets when the last sync started.
    /// </summary>
    DateTime? LastSyncStartedUtc { get; }

    /// <summary>
    /// Gets when the last sync finished.
    /// </summary>
    DateTime? LastSyncCompletedUtc { get; }

    /// <summary>
    /// Gets the error of the last sync, or null when it succeeded.
    /// </summary>
    string? LastSyncError { get; }

    /// <summary>
    /// Runs a sync now and waits for it. A sync that is already running is joined instead.
    /// </summary>
    /// <param name="progress">Progress in percent.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the sync finished.</returns>
    Task SyncAsync(IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a sync in the background unless one is already running.
    /// </summary>
    /// <returns>True when a sync was started, false when one was already running.</returns>
    bool StartSyncInBackground();
}
