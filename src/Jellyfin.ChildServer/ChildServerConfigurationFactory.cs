using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.ChildServer;

namespace Jellyfin.ChildServer;

/// <summary>
/// Registers the <c>childserver</c> configuration store, persisted as <c>childserver.xml</c>.
/// </summary>
public class ChildServerConfigurationFactory : IConfigurationFactory
{
    /// <inheritdoc />
    public IEnumerable<ConfigurationStore> GetConfigurations()
    {
        yield return new ConfigurationStore
        {
            ConfigurationType = typeof(ChildServerConfiguration),
            Key = ChildServerManager.ConfigurationKey
        };
    }
}
