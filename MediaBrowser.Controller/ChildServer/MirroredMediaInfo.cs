using System;
using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.ChildServer;

/// <summary>
/// What the parent server knows about a mirrored media file, used instead of probing the placeholder.
/// </summary>
public class MirroredMediaInfo
{
    /// <summary>
    /// Gets or sets the id of the item on the parent server.
    /// </summary>
    public string ParentItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the id of the media source on the parent server.
    /// </summary>
    public string? ParentMediaSourceId { get; set; }

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
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the media streams reported by the parent server.
    /// </summary>
    public IReadOnlyList<MediaStream> MediaStreams { get; set; } = Array.Empty<MediaStream>();
}
