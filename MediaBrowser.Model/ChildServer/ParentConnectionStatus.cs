namespace MediaBrowser.Model.ChildServer;

/// <summary>
/// The outcome of an attempt to reach and sign in to the parent server.
/// </summary>
public enum ParentConnectionStatus
{
    /// <summary>
    /// The parent server answered and the credentials were accepted.
    /// </summary>
    Success = 0,

    /// <summary>
    /// The parent server rejected the user name or password.
    /// </summary>
    InvalidCredentials = 1,

    /// <summary>
    /// The parent server, or a proxy in front of it, refused the request before sign in.
    /// </summary>
    AccessDenied = 2,

    /// <summary>
    /// The parent server could not be reached.
    /// </summary>
    ConnectionFailed = 3,

    /// <summary>
    /// Something answered at the URL, but it did not behave like a Jellyfin server.
    /// </summary>
    InvalidResponse = 4,

    /// <summary>
    /// No parent server has been configured yet.
    /// </summary>
    NotConfigured = 5
}
