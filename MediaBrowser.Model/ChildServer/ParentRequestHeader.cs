namespace MediaBrowser.Model.ChildServer;

/// <summary>
/// An HTTP header sent with every request to the parent server.
/// </summary>
public class ParentRequestHeader
{
    /// <summary>
    /// Gets or sets the header name, for example <c>CF-Access-Client-Id</c>.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the header value.
    /// </summary>
    public string Value { get; set; } = string.Empty;
}
