namespace MediaBrowser.Controller.ChildServer;

/// <summary>
/// Totals about the mirrored media files on this device.
/// </summary>
/// <param name="MirroredItemCount">How many media items are mirrored from the parent.</param>
/// <param name="CachedItemCount">How many of them are fully stored on this device.</param>
/// <param name="CachedBytes">How many bytes the cached files occupy.</param>
/// <param name="ActiveDownloads">How many downloads are queued or running.</param>
public readonly record struct ChildCacheStatistics(int MirroredItemCount, int CachedItemCount, long CachedBytes, int ActiveDownloads);
