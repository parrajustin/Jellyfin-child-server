using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.ChildServer.Tests;

/// <summary>
/// Answers requests from a script keyed by request path and records every request it saw.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public IList<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();

    public Exception? FailWith { get; set; }

    public StubHttpMessageHandler Map(string absolutePath, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _routes[absolutePath] = responder;
        return this;
    }

    public StubHttpMessageHandler MapJson(string absolutePath, HttpStatusCode statusCode, string json)
        => Map(absolutePath, _ => Json(statusCode, json));

    public static HttpResponseMessage Json(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    public static HttpResponseMessage Html(HttpStatusCode statusCode, string html)
        => new(statusCode)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (FailWith is not null)
        {
            throw FailWith;
        }

        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (_routes.TryGetValue(path, out var responder))
        {
            return Task.FromResult(responder(request));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
