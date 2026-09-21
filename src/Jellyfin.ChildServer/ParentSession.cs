namespace Jellyfin.ChildServer;

/// <summary>
/// A signed in session on the parent server.
/// </summary>
/// <param name="Endpoint">Where the parent lives and which headers it needs.</param>
/// <param name="AccessToken">The access token of the child's sign in.</param>
/// <param name="UserId">The id of the parent user the child signed in as.</param>
public sealed record ParentSession(ParentEndpoint Endpoint, string AccessToken, string UserId);
