using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jellyfin.ChildServer.Pages;

/// <summary>
/// Rewrites the two web client files the child server serves itself, so the bundled
/// jellyfin-web loads the child server plugin without being forked.
/// </summary>
public static partial class ChildServerWebClient
{
    /// <summary>
    /// The name under which the plugin registers itself on <c>window</c>; the web client's plugin
    /// loader accepts such names in the <c>plugins</c> list of its config file.
    /// </summary>
    public const string PluginName = "ChildServerPlugin";

    /// <summary>
    /// The file name, relative to <c>/web/</c>, from which the plugin script is served.
    /// </summary>
    public const string ScriptFileName = "childserver-plugin.js";

    private const string ScriptMarker = "data-childserver=\"plugin\"";

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Gets the script tag that loads the plugin: a classic script placed at the top of the head,
    /// so it runs before the deferred application bundle registers plugins.
    /// </summary>
    /// <param name="version">A version string used to bust caches.</param>
    /// <returns>The script tag.</returns>
    public static string ScriptTag(string version)
        => "<script src=\"" + ScriptFileName + "?v=" + Uri.EscapeDataString(version) + "\" " + ScriptMarker + "></script>";

    /// <summary>
    /// Inserts the plugin script tag right after the opening head tag of the web client's index page.
    /// </summary>
    /// <param name="indexHtml">The index page.</param>
    /// <param name="version">A version string used to bust caches.</param>
    /// <returns>The page with the script tag; unchanged when it already carries one.</returns>
    public static string InjectScript(string indexHtml, string version)
    {
        ArgumentNullException.ThrowIfNull(indexHtml);

        if (indexHtml.Contains(ScriptMarker, StringComparison.Ordinal))
        {
            return indexHtml;
        }

        var tag = ScriptTag(version);
        var head = HeadTag().Match(indexHtml);
        if (!head.Success)
        {
            return tag + indexHtml;
        }

        return indexHtml.Insert(head.Index + head.Length, "\n    " + tag);
    }

    /// <summary>
    /// Adds the plugin to the <c>plugins</c> list of the web client's config file.
    /// </summary>
    /// <param name="configJson">The config file content.</param>
    /// <returns>The updated content; unchanged when the plugin is already listed or the file is not JSON.</returns>
    public static string AddPlugin(string configJson)
    {
        ArgumentNullException.ThrowIfNull(configJson);

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(configJson)?.AsObject();
        }
        catch (JsonException)
        {
            return configJson;
        }

        if (root is null)
        {
            return configJson;
        }

        if (root["plugins"] is not JsonArray plugins)
        {
            plugins = new JsonArray();
            root["plugins"] = plugins;
        }

        if (plugins.Any(p => p is JsonValue value && value.TryGetValue<string>(out var name) && string.Equals(name, PluginName, StringComparison.Ordinal)))
        {
            return configJson;
        }

        plugins.Add(PluginName);
        return root.ToJsonString(_jsonOptions) + "\n";
    }

    [GeneratedRegex("<head[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadTag();
}
