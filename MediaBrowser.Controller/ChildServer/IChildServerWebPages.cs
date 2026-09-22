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
}
