using System;

namespace MediaBrowser.Model.ChildServer;

/// <summary>
/// The editable child server settings as exchanged with administrators. Secrets are never returned.
/// </summary>
public class ChildServerSettings
{
    /// <summary>
    /// Gets or sets the base URL of the parent server.
    /// </summary>
    public string? ParentUrl { get; set; }

    /// <summary>
    /// Gets or sets the user name used to sign in to the parent server.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Gets or sets a new password. Only sent by clients; never returned. An empty value keeps the stored password.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a password is stored.
    /// </summary>
    public bool HasPassword { get; set; }

    /// <summary>
    /// Gets or sets extra HTTP headers sent with every request to the parent server.
    /// </summary>
    public ParentRequestHeader[] CustomHeaders { get; set; } = Array.Empty<ParentRequestHeader>();

    /// <summary>
    /// Gets or sets how many upcoming episodes are downloaded while an episode is being watched.
    /// </summary>
    public int PrefetchEpisodeCount { get; set; } = 4;

    /// <summary>
    /// Gets or sets the maximum size of the local media cache in megabytes. Zero disables the limit.
    /// </summary>
    public int MaxCacheSizeMb { get; set; } = 5120;

    /// <summary>
    /// Gets or sets how long a playback request waits for a download before giving up, in seconds.
    /// </summary>
    public int DownloadWaitTimeoutSeconds { get; set; } = 900;

    /// <summary>
    /// Gets or sets how often the parent library is mirrored, in hours.
    /// </summary>
    public int SyncIntervalHours { get; set; } = 6;
}
