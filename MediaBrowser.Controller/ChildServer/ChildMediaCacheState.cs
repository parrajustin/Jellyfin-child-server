namespace MediaBrowser.Controller.ChildServer;

/// <summary>
/// The cache state of one mirrored media file.
/// </summary>
/// <param name="IsManaged">Whether the file is mirrored from the parent server at all.</param>
/// <param name="IsCached">Whether the whole file is stored on this device.</param>
/// <param name="IsDownloading">Whether a download is queued or running.</param>
/// <param name="DownloadedBytes">How many bytes are on this device.</param>
/// <param name="ExpectedBytes">The size of the file on the parent server.</param>
public readonly record struct ChildMediaCacheState(bool IsManaged, bool IsCached, bool IsDownloading, long DownloadedBytes, long ExpectedBytes)
{
    /// <summary>
    /// Gets the state of a file that is not mirrored from the parent server.
    /// </summary>
    public static ChildMediaCacheState Unmanaged => new(false, false, false, 0, 0);
}
