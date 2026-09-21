using System;

namespace MediaBrowser.Model.ChildServer;

/// <summary>
/// Persisted configuration of the child server: how to reach the parent Jellyfin server
/// and how much of its media to keep on the local device.
/// </summary>
public class ChildServerConfiguration
{
    /// <summary>
    /// Gets or sets the base URL of the parent Jellyfin server, for example <c>https://media.example.com</c>.
    /// </summary>
    public string? ParentUrl { get; set; }

    /// <summary>
    /// Gets or sets the user name used to sign in to the parent server.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Gets or sets the password used to sign in to the parent server.
    /// It is kept so the child can sign in again when its access token stops working.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets the access token obtained from the parent server on the last successful sign in.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// Gets or sets the id of the parent user the child signs in as.
    /// </summary>
    public string? ParentUserId { get; set; }

    /// <summary>
    /// Gets or sets the id reported by the parent server.
    /// </summary>
    public string? ParentServerId { get; set; }

    /// <summary>
    /// Gets or sets the name reported by the parent server.
    /// </summary>
    public string? ParentServerName { get; set; }

    /// <summary>
    /// Gets or sets extra HTTP headers sent with every request to the parent server,
    /// for example Cloudflare Access service token headers.
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

    /// <summary>
    /// Gets or sets the folder that holds the mirrored library. Defaults to a folder inside the data directory.
    /// </summary>
    public string? LibraryPath { get; set; }
}
