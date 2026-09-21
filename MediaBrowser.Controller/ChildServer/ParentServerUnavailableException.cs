using System;

namespace MediaBrowser.Controller.ChildServer;

/// <summary>
/// Raised when media has to come from the parent server and the parent cannot be reached.
/// </summary>
public class ParentServerUnavailableException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ParentServerUnavailableException"/> class.
    /// </summary>
    public ParentServerUnavailableException()
        : base("Can't connect to parent server.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ParentServerUnavailableException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public ParentServerUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ParentServerUnavailableException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ParentServerUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
