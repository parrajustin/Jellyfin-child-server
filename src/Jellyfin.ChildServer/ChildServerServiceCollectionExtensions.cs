using Jellyfin.ChildServer.Cache;
using Jellyfin.ChildServer.Mirror;
using Jellyfin.ChildServer.Pages;
using MediaBrowser.Controller.ChildServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.ChildServer;

/// <summary>
/// Registers the child server services.
/// </summary>
public static class ChildServerServiceCollectionExtensions
{
    /// <summary>
    /// Adds the services that link this server to its parent server and mirror its media.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddChildServer(this IServiceCollection services)
    {
        services.AddSingleton<ParentServerClient>();
        services.AddSingleton<ChildServerManager>();
        services.AddSingleton<IChildServerManager>(provider => provider.GetRequiredService<ChildServerManager>());
        services.AddSingleton<ChildServerPaths>();
        services.AddSingleton(provider =>
        {
            var paths = provider.GetRequiredService<ChildServerPaths>();
            return new MirrorManifest(() => paths.ManifestPath, provider.GetRequiredService<ILogger<MirrorManifest>>());
        });
        services.AddSingleton<ParentLibraryReader>();
        services.AddSingleton<ChildMediaCache>();
        services.AddSingleton<IChildServerMediaCache>(provider => provider.GetRequiredService<ChildMediaCache>());
        services.AddSingleton<IChildServerLibrarySync, ChildLibraryMirror>();
        services.AddSingleton<IChildServerWebPages, ChildServerWebPages>();
        return services;
    }
}
