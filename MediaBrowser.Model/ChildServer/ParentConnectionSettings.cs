using System;

namespace MediaBrowser.Model.ChildServer;

/// <summary>
/// The details needed to sign in to a parent server. Used to test a connection before it is saved.
/// </summary>
public class ParentConnectionSettings
{
    /// <summary>
    /// Gets or sets the base URL of the parent server.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Gets or sets the user name.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Gets or sets the password. When null the password stored in the configuration is used.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets extra HTTP headers sent with every request.
    /// </summary>
    public ParentRequestHeader[] CustomHeaders { get; set; } = Array.Empty<ParentRequestHeader>();
}
