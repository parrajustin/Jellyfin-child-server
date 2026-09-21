using System;
using System.Collections.Generic;
using MediaBrowser.Model.ChildServer;

namespace Jellyfin.ChildServer;

/// <summary>
/// Where a parent server lives and which extra headers every request to it needs.
/// </summary>
public sealed class ParentEndpoint
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ParentEndpoint"/> class.
    /// </summary>
    /// <param name="baseUrl">The normalized base URL, ending with a slash.</param>
    /// <param name="headers">The extra headers, may be empty.</param>
    public ParentEndpoint(Uri baseUrl, IReadOnlyList<ParentRequestHeader> headers)
    {
        BaseUrl = baseUrl;
        Headers = headers;
    }

    /// <summary>
    /// Gets the base URL, ending with a slash so relative paths can be appended.
    /// </summary>
    public Uri BaseUrl { get; }

    /// <summary>
    /// Gets the extra headers sent with every request.
    /// </summary>
    public IReadOnlyList<ParentRequestHeader> Headers { get; }
}
