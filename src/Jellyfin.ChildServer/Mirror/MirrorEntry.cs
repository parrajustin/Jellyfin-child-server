using System;
using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace Jellyfin.ChildServer.Mirror;

/// <summary>
/// One media file mirrored from the parent server, as recorded in the mirror manifest.
/// </summary>
public sealed class MirrorEntry
{
    /// <summary>
    /// Gets or sets the absolute local path of the media file (placeholder or cached copy).
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the parent library the item came from.
    /// </summary>
    public string View { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the id of the item on the parent server.
    /// </summary>
    public string ParentItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the id of the media source on the parent server.
    /// </summary>
    public string? ParentMediaSourceId { get; set; }

    /// <summary>
    /// Gets or sets the item kind, for example <c>Movie</c> or <c>Episode</c>.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name of the item.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the size of the file on the parent server in bytes.
    /// </summary>
    public long ExpectedSize { get; set; }

    /// <summary>
    /// Gets or sets the container, for example <c>mkv</c>.
    /// </summary>
    public string? Container { get; set; }

    /// <summary>
    /// Gets or sets the duration in ticks.
    /// </summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets the total bitrate.
    /// </summary>
    public int? Bitrate { get; set; }

    /// <summary>
    /// Gets or sets the media streams reported by the parent server.
    /// </summary>
    public IReadOnlyList<MediaStream> MediaStreams { get; set; } = Array.Empty<MediaStream>();

    /// <summary>
    /// Gets or sets the parent's etag of the item, used to detect changes.
    /// </summary>
    public string? Etag { get; set; }

    /// <summary>
    /// Gets or sets when the item was last seen on the parent.
    /// </summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the whole file is stored on this device.
    /// </summary>
    public bool IsCached { get; set; }

    /// <summary>
    /// Gets or sets when the file was last played or fetched, used to pick what to evict first.
    /// </summary>
    public DateTime? LastAccessUtc { get; set; }
}
