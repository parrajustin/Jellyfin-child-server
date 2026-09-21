namespace MediaBrowser.Model.ChildServer;

/// <summary>
/// The result of testing or establishing the connection to the parent server.
/// </summary>
public class ParentConnectionResult
{
    /// <summary>
    /// Gets or sets the outcome.
    /// </summary>
    public ParentConnectionStatus Status { get; set; }

    /// <summary>
    /// Gets or sets a human readable description of the outcome.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name reported by the parent server, when it answered.
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// Gets or sets the version reported by the parent server, when it answered.
    /// </summary>
    public string? ServerVersion { get; set; }

    /// <summary>
    /// Gets or sets the id reported by the parent server, when it answered.
    /// </summary>
    public string? ServerId { get; set; }

    /// <summary>
    /// Gets or sets the id of the signed in parent user, on success.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Gets a value indicating whether the connection succeeded.
    /// </summary>
    public bool IsSuccess => Status == ParentConnectionStatus.Success;
}
