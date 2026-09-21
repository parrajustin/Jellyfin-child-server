using MediaBrowser.Controller.ChildServer;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.ChildServer;

/// <summary>
/// Registers the child server services.
/// </summary>
public static class ChildServerServiceCollectionExtensions
{
    /// <summary>
    /// Adds the services that link this server to its parent server.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddChildServer(this IServiceCollection services)
    {
        services.AddSingleton<ParentServerClient>();
        services.AddSingleton<IChildServerManager, ChildServerManager>();
        return services;
    }
}
