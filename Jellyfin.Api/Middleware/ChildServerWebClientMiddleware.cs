using System;
using System.IO;
using System.Net.Mime;
using System.Threading.Tasks;
using MediaBrowser.Controller.ChildServer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Api.Middleware;

/// <summary>
/// Serves the three web client files the child server changes: the plugin script, the index page with the
/// script tag added, and the config file with the plugin listed. Everything else falls through to the
/// normal static file hosting.
/// </summary>
public class ChildServerWebClientMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IChildServerWebPages _pages;
    private readonly ILogger<ChildServerWebClientMiddleware> _logger;
    private readonly string _webPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildServerWebClientMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next delegate in the pipeline.</param>
    /// <param name="pages">The child server web pages.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="webPath">The folder that holds the web client.</param>
    public ChildServerWebClientMiddleware(RequestDelegate next, IChildServerWebPages pages, ILogger<ChildServerWebClientMiddleware> logger, string webPath)
    {
        _next = next;
        _pages = pages;
        _logger = logger;
        _webPath = webPath;
    }

    /// <summary>
    /// Serves the request when it asks for one of the rewritten files.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <returns>The async task.</returns>
    public async Task Invoke(HttpContext httpContext)
    {
        var request = httpContext.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            await _next(httpContext).ConfigureAwait(false);
            return;
        }

        var path = request.Path.Value ?? string.Empty;
        if (string.Equals(path, "/web/" + _pages.WebClientScriptFileName, StringComparison.OrdinalIgnoreCase))
        {
            await ServeScriptAsync(httpContext).ConfigureAwait(false);
            return;
        }

        if (string.Equals(path, "/web", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/web/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/web/index.html", StringComparison.OrdinalIgnoreCase))
        {
            if (await ServeRewrittenAsync(httpContext, "index.html", MediaTypeNames.Text.Html, _pages.InjectWebClientScript).ConfigureAwait(false))
            {
                return;
            }
        }
        else if (string.Equals(path, "/web/config.json", StringComparison.OrdinalIgnoreCase))
        {
            if (await ServeRewrittenAsync(httpContext, "config.json", MediaTypeNames.Application.Json, _pages.AddWebClientPlugin).ConfigureAwait(false))
            {
                return;
            }
        }

        await _next(httpContext).ConfigureAwait(false);
    }

    private async Task ServeScriptAsync(HttpContext httpContext)
    {
        var script = _pages.OpenWebClientScript();
        if (script is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var (content, contentType) = script.Value;
        await using (content.ConfigureAwait(false))
        {
            httpContext.Response.ContentType = contentType;
            httpContext.Response.Headers.CacheControl = new StringValues("public, max-age=3600");
            if (HttpMethods.IsHead(httpContext.Request.Method))
            {
                return;
            }

            await content.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }

    private async Task<bool> ServeRewrittenAsync(HttpContext httpContext, string fileName, string contentType, Func<string, string> rewrite)
    {
        var filePath = Path.Combine(_webPath, fileName);
        if (!File.Exists(filePath))
        {
            return false;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(filePath, httpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read {File} from the web client folder", filePath);
            return false;
        }

        var body = rewrite(text);
        httpContext.Response.ContentType = contentType + "; charset=utf-8";
        httpContext.Response.Headers.CacheControl = new StringValues("no-cache");
        if (HttpMethods.IsHead(httpContext.Request.Method))
        {
            return true;
        }

        await httpContext.Response.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
        return true;
    }
}
