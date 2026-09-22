using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Mime;
using System.Reflection;
using MediaBrowser.Controller.ChildServer;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.ChildServer.Pages;

/// <summary>
/// Serves the child server's dashboard page and web client plugin from embedded resources.
/// </summary>
public class ChildServerWebPages : IChildServerWebPages
{
    /// <summary>
    /// The name of the parent server settings page, used in <c>#/configurationpage?name=...</c>.
    /// </summary>
    public const string ParentServerPageName = "ChildServerParent";

    private const string ResourcePrefix = "Jellyfin.ChildServer.Pages.";
    private const string ScriptResource = ResourcePrefix + "childserver-plugin.js";
    private const string JavaScriptContentType = "text/javascript";

    private static readonly Dictionary<string, (string Resource, string ContentType)> _pages = new(StringComparer.OrdinalIgnoreCase)
    {
        [ParentServerPageName] = (ResourcePrefix + "childserver-parent.html", MediaTypeNames.Text.Html)
    };

    private static readonly string _version = typeof(ChildServerWebPages).Assembly.GetName().Version?.ToString(3) ?? "0";

    /// <inheritdoc />
    public string WebClientScriptFileName => ChildServerWebClient.ScriptFileName;

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

        var stream = OpenResource(page.Resource);
        return stream is null ? null : (stream, page.ContentType);
    }

    /// <inheritdoc />
    public (Stream Content, string ContentType)? OpenWebClientScript()
    {
        var stream = OpenResource(ScriptResource);
        return stream is null ? null : (stream, JavaScriptContentType);
    }

    /// <inheritdoc />
    public string InjectWebClientScript(string indexHtml) => ChildServerWebClient.InjectScript(indexHtml, _version);

    /// <inheritdoc />
    public string AddWebClientPlugin(string configJson) => ChildServerWebClient.AddPlugin(configJson);

    private static Stream? OpenResource(string resource)
        => typeof(ChildServerWebPages).Assembly.GetManifestResourceStream(resource);
}
