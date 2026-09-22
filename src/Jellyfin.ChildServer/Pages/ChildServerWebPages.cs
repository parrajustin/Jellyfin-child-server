using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Mime;
using MediaBrowser.Controller.ChildServer;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.ChildServer.Pages;

/// <summary>
/// Serves the child server's dashboard page from embedded resources.
/// </summary>
public class ChildServerWebPages : IChildServerWebPages
{
    /// <summary>
    /// The name of the parent server settings page, used in <c>#/configurationpage?name=...</c>.
    /// </summary>
    public const string ParentServerPageName = "ChildServerParent";

    private const string ResourcePrefix = "Jellyfin.ChildServer.Pages.";

    private static readonly Dictionary<string, (string Resource, string ContentType)> _pages = new(StringComparer.OrdinalIgnoreCase)
    {
        [ParentServerPageName] = (ResourcePrefix + "childserver-parent.html", MediaTypeNames.Text.Html)
    };

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = ParentServerPageName,
            DisplayName = "Parent server",
            EmbeddedResourcePath = _pages[ParentServerPageName].Resource,
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "hub"
        };
    }

    /// <inheritdoc />
    public (Stream Content, string ContentType)? Open(string name)
    {
        if (string.IsNullOrEmpty(name) || !_pages.TryGetValue(name, out var page))
        {
            return null;
        }

        var stream = typeof(ChildServerWebPages).Assembly.GetManifestResourceStream(page.Resource);
        return stream is null ? null : (stream, page.ContentType);
    }
}
