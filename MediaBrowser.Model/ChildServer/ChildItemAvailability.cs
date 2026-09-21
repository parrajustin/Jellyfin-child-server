namespace MediaBrowser.Model.ChildServer;

/// <summary>
/// Whether an item's media is present on this child server or still has to come from the parent.
/// </summary>
public class ChildItemAvailability
{
    /// <summary>
    /// Gets or sets a value indicating whether the item is mirrored from the parent server.
    /// Items that are not managed play from local files like on any Jellyfin server.
    /// </summary>
    public bool IsManaged { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the whole media file is stored on this device.
    /// </summary>
    public bool IsCached { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a download from the parent is in progress.
    /// </summary>
    public bool IsDownloading { get; set; }

    /// <summary>
    /// Gets or sets how many bytes of the media file are on this device.
    /// </summary>
    public long DownloadedBytes { get; set; }

    /// <summary>
    /// Gets or sets the size of the media file on the parent server.
    /// </summary>
    public long ExpectedBytes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the parent server answered a reachability check just now.
    /// </summary>
    public bool ParentReachable { get; set; }
}
