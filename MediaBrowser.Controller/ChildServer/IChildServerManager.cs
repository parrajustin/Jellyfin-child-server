using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.ChildServer;

namespace MediaBrowser.Controller.ChildServer;

/// <summary>
/// Owns the link between this child server and its parent Jellyfin server:
/// the stored settings, the sign in state and connection checks.
/// </summary>
public interface IChildServerManager
{
    /// <summary>
    /// Gets the persisted configuration.
    /// </summary>
    ChildServerConfiguration Configuration { get; }

    /// <summary>
    /// Gets a value indicating whether a parent server URL and user name are configured.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Gets the editable settings without secrets.
    /// </summary>
    /// <returns>The settings.</returns>
    ChildServerSettings GetSettings();

    /// <summary>
    /// Validates and saves settings. An empty password keeps the stored one; changing the URL,
    /// user name or password drops the stored access token so the next connection signs in again.
    /// </summary>
    /// <param name="settings">The settings to save.</param>
    void SaveSettings(ChildServerSettings settings);

    /// <summary>
    /// Gets the current connection state.
    /// </summary>
    /// <returns>The status.</returns>
    ChildServerStatus GetStatus();

    /// <summary>
    /// Tries to reach the given parent server and sign in with the given credentials. Nothing is saved.
    /// </summary>
    /// <param name="settings">The connection details to try.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<ParentConnectionResult> TestConnectionAsync(ParentConnectionSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Makes sure the child is signed in to the configured parent server, reusing the stored access token
    /// when it still works and signing in again otherwise. The result is persisted.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<ParentConnectionResult> ConnectAsync(CancellationToken cancellationToken);
}
