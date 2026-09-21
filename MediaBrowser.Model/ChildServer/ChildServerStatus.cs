using System;

namespace MediaBrowser.Model.ChildServer;

/// <summary>
/// The current state of the link between this child server and its parent.
/// </summary>
public class ChildServerStatus
{
    /// <summary>
    /// Gets or sets a value indicating whether a parent server URL and user name are configured.
    /// </summary>
    public bool IsConfigured { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the child holds an access token for the parent.
    /// </summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// Gets or sets the name reported by the parent server.
    /// </summary>
    public string? ParentServerName { get; set; }

    /// <summary>
    /// Gets or sets the version reported by the parent server on the last successful check.
    /// </summary>
    public string? ParentServerVersion { get; set; }

    /// <summary>
    /// Gets or sets the id of the parent user the child signs in as.
    /// </summary>
    public string? ParentUserId { get; set; }

    /// <summary>
    /// Gets or sets the outcome of the last connection attempt.
    /// </summary>
    public ParentConnectionStatus? LastConnectionStatus { get; set; }

    /// <summary>
    /// Gets or sets the message of the last connection attempt.
    /// </summary>
    public string? LastConnectionMessage { get; set; }

    /// <summary>
    /// Gets or sets when the last connection attempt happened.
    /// </summary>
    public DateTime? LastConnectionAttemptUtc { get; set; }

    /// <summary>
    /// Gets or sets whether a library sync is running.
    /// </summary>
    public ChildSyncState SyncState { get; set; }

    /// <summary>
    /// Gets or sets when the last sync started.
    /// </summary>
    public DateTime? LastSyncStartedUtc { get; set; }

    /// <summary>
    /// Gets or sets when the last sync finished, whether it succeeded or failed.
    /// </summary>
    public DateTime? LastSyncCompletedUtc { get; set; }

    /// <summary>
    /// Gets or sets the error of the last sync, or null when it succeeded.
    /// </summary>
    public string? LastSyncError { get; set; }

    /// <summary>
    /// Gets or sets how many media items are mirrored from the parent.
    /// </summary>
    public int MirroredItemCount { get; set; }

    /// <summary>
    /// Gets or sets how many mirrored items are fully stored on this device.
    /// </summary>
    public int CachedItemCount { get; set; }

    /// <summary>
    /// Gets or sets how many bytes the cached media files occupy.
    /// </summary>
    public long CachedBytes { get; set; }

    /// <summary>
    /// Gets or sets how many downloads from the parent are queued or running.
    /// </summary>
    public int ActiveDownloads { get; set; }
}
