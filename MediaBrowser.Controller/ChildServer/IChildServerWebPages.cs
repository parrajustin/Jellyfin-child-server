using System.Collections.Generic;
using System.IO;
using MediaBrowser.Model.Plugins;

namespace MediaBrowser.Controller.ChildServer;

/// <summary>
/// Dashboard pages the child server adds to the web client, served like plugin configuration pages.
/// </summary>
public interface IChildServerWebPages
{
    /// <summary>
    /// Gets the file name, relative to the web client root, from which the web client plugin script is served.
    /// </summary>
    string WebClientScriptFileName { get; }

    /// <summary>
    /// Gets the pages, for the dashboard menu.
    /// </summary>
    /// <returns>The page descriptions.</returns>
    IEnumerable<PluginPageInfo> GetPages();

    /// <summary>
    /// Opens the content of a page.
    /// </summary>
    /// <param name="name">The page name as listed by <see cref="GetPages"/>.</param>
    /// <returns>The content stream and its content type, or null when there is no such page.</returns>
    (Stream Content, string ContentType)? Open(string name);

    /// <summary>
    /// Opens the web client plugin script.
    /// </summary>
    /// <returns>The script and its content type.</returns>
    (Stream Content, string ContentType)? OpenWebClientScript();

    /// <summary>
    /// Adds the plugin script tag to the web client's index page.
    /// </summary>
    /// <param name="indexHtml">The page as shipped with the web client.</param>
    /// <returns>The page to serve.</returns>
    string InjectWebClientScript(string indexHtml);

    /// <summary>
    /// Lists the plugin in the web client's config file.
    /// </summary>
    /// <param name="configJson">The config file as shipped with the web client.</param>
    /// <returns>The config file to serve.</returns>
    string AddWebClientPlugin(string configJson);
}
