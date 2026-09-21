namespace MediaBrowser.Model.ChildServer;

/// <summary>
/// Whether the parent library is being mirrored right now.
/// </summary>
public enum ChildSyncState
{
    /// <summary>
    /// No sync is running.
    /// </summary>
    Idle = 0,

    /// <summary>
    /// A sync is running.
    /// </summary>
    Running = 1
}
