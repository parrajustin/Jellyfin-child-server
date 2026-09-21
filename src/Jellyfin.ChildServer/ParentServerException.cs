using System;
using MediaBrowser.Model.ChildServer;

namespace Jellyfin.ChildServer;

/// <summary>
/// Raised when the parent server cannot be reached, refuses the request or answers with something unexpected.
/// </summary>
public class ParentServerException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ParentServerException"/> class.
    /// </summary>
    /// <param name="status">What went wrong.</param>
    /// <param name="message">A message suitable for showing to an administrator.</param>
    public ParentServerException(ParentConnectionStatus status, string message)
        : base(message)
    {
        Status = status;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ParentServerException"/> class.
    /// </summary>
    /// <param name="status">What went wrong.</param>
    /// <param name="message">A message suitable for showing to an administrator.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ParentServerException(ParentConnectionStatus status, string message, Exception innerException)
        : base(message, innerException)
    {
        Status = status;
    }

    /// <summary>
    /// Gets what went wrong.
    /// </summary>
    public ParentConnectionStatus Status { get; }
}
