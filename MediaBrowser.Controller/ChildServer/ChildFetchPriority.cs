namespace MediaBrowser.Controller.ChildServer;

/// <summary>
/// How urgently a media file is needed from the parent server. Lower values are served first.
/// </summary>
public enum ChildFetchPriority
{
    /// <summary>
    /// Someone is trying to play the item right now.
    /// </summary>
    Playback = 0,

    /// <summary>
    /// The item will probably be played soon, for example the next episode.
    /// </summary>
    Prefetch = 1,

    /// <summary>
    /// Nobody is waiting for the item.
    /// </summary>
    Background = 2
}
