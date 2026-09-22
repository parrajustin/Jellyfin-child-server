using System;
using System.IO;
using System.Text.Json;
using Jellyfin.ChildServer.Pages;
using Xunit;

namespace Jellyfin.ChildServer.Tests;

public class ChildServerWebClientTests
{
    private const string IndexHtml = "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n    <meta charset=\"utf-8\">\n    <title>Jellyfin</title>\n    <script defer src=\"main.jellyfin.bundle.js\"></script>\n</head>\n<body dir=\"ltr\">\n    <div id=\"reactRoot\"></div>\n</body>\n</html>\n";

    [Fact]
    public void InjectScript_PutsTheScriptTagAtTheTopOfTheHead()
    {
        var result = ChildServerWebClient.InjectScript(IndexHtml, "12.1.0");

        var scriptIndex = result.IndexOf(ChildServerWebClient.ScriptFileName, StringComparison.Ordinal);
        var bundleIndex = result.IndexOf("main.jellyfin.bundle.js", StringComparison.Ordinal);
        Assert.True(scriptIndex > 0);
        Assert.True(scriptIndex < bundleIndex, "the plugin script must load before the application bundle");
        Assert.Contains("<script src=\"childserver-plugin.js?v=12.1.0\" data-childserver=\"plugin\"></script>", result, StringComparison.Ordinal);
        Assert.DoesNotContain("defer src=\"childserver", result, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectScript_IsIdempotent()
    {
        var once = ChildServerWebClient.InjectScript(IndexHtml, "1");
        var twice = ChildServerWebClient.InjectScript(once, "1");

        Assert.Equal(once, twice);
    }

    [Fact]
    public void InjectScript_WithoutAHeadTag_PrependsTheScript()
    {
        var result = ChildServerWebClient.InjectScript("<html><body></body></html>", "1");

        Assert.StartsWith("<script src=\"childserver-plugin.js", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPlugin_AppendsTheWindowPluginName()
    {
        const string config = "{\n  \"includeCorsCredentials\": false,\n  \"multiserver\": false,\n  \"themes\": [],\n  \"menuLinks\": [],\n  \"servers\": [],\n  \"plugins\": [\n    \"playAccessValidation/plugin\",\n    \"syncPlay/plugin\"\n  ]\n}\n";

        var result = ChildServerWebClient.AddPlugin(config);

        using var document = JsonDocument.Parse(result);
        var plugins = document.RootElement.GetProperty("plugins");
        Assert.Equal(3, plugins.GetArrayLength());
        Assert.Equal("playAccessValidation/plugin", plugins[0].GetString());
        Assert.Equal("syncPlay/plugin", plugins[1].GetString());
        Assert.Equal(ChildServerWebClient.PluginName, plugins[2].GetString());
        Assert.False(document.RootElement.GetProperty("multiserver").GetBoolean());
        Assert.Equal(ChildServerWebClient.AddPlugin(result), result);
    }

    [Fact]
    public void AddPlugin_CreatesThePluginListWhenMissing()
    {
        var result = ChildServerWebClient.AddPlugin("{\"servers\":[]}");

        using var document = JsonDocument.Parse(result);
        Assert.Equal(ChildServerWebClient.PluginName, document.RootElement.GetProperty("plugins")[0].GetString());
    }

    [Fact]
    public void AddPlugin_LeavesInvalidJsonAlone()
    {
        Assert.Equal("not json", ChildServerWebClient.AddPlugin("not json"));
    }

    [Fact]
    public void WebPages_ServeTheEmbeddedScriptAndPage()
    {
        var pages = new ChildServerWebPages();

        var script = pages.OpenWebClientScript();
        Assert.NotNull(script);
        using (var reader = new StreamReader(script.Value.Content))
        {
            var text = reader.ReadToEnd();
            Assert.Contains("window.ChildServerPlugin", text, StringComparison.Ordinal);
            Assert.Contains("preplayintercept", text, StringComparison.Ordinal);
        }

        Assert.Equal("text/javascript", script.Value.ContentType);
        Assert.Equal("childserver-plugin.js", pages.WebClientScriptFileName);

        var page = pages.Open(ChildServerWebPages.ParentServerPageName);
        Assert.NotNull(page);
        page.Value.Content.Dispose();
        Assert.Null(pages.Open("nope"));
    }
}
