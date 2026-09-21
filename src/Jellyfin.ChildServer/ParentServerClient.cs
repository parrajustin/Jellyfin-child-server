using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Model.ChildServer;
using MediaBrowser.Model.System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.ChildServer;

/// <summary>
/// Low level HTTP client for a parent Jellyfin server. Every request carries the Jellyfin
/// authorization header that identifies this child as a client, plus any custom headers
/// the administrator configured (for example Cloudflare Access service tokens).
/// </summary>
public class ParentServerClient
{
    /// <summary>
    /// The client name the parent sees in its device list.
    /// </summary>
    public const string ClientName = "Jellyfin Child Server";

    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<ParentServerClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ParentServerClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="appHost">The application host, used to identify this child to the parent.</param>
    /// <param name="logger">The logger.</param>
    public ParentServerClient(IHttpClientFactory httpClientFactory, IServerApplicationHost appHost, ILogger<ParentServerClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _appHost = appHost;
        _logger = logger;
    }

    /// <summary>
    /// Turns what an administrator typed into a usable base URL. A missing scheme defaults to http.
    /// </summary>
    /// <param name="url">The URL as typed.</param>
    /// <returns>The normalized URL ending with a slash, or null when it is not an absolute http(s) URL.</returns>
    public static Uri? TryParseBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "http://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    /// <summary>
    /// Reads the public system information, which needs no sign in.
    /// </summary>
    /// <param name="endpoint">The parent server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The public system information.</returns>
    public Task<PublicSystemInfo> GetPublicSystemInfoAsync(ParentEndpoint endpoint, CancellationToken cancellationToken)
        => GetJsonAsync<PublicSystemInfo>(endpoint, "System/Info/Public", null, cancellationToken);

    /// <summary>
    /// Reads the full system information, which proves an access token still works.
    /// </summary>
    /// <param name="endpoint">The parent server.</param>
    /// <param name="accessToken">The access token to use.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The system information.</returns>
    public Task<SystemInfo> GetSystemInfoAsync(ParentEndpoint endpoint, string accessToken, CancellationToken cancellationToken)
        => GetJsonAsync<SystemInfo>(endpoint, "System/Info", accessToken, cancellationToken);

    /// <summary>
    /// Signs in with a user name and password.
    /// </summary>
    /// <param name="endpoint">The parent server.</param>
    /// <param name="username">The user name.</param>
    /// <param name="password">The password, may be empty.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The authentication result including the access token.</returns>
    /// <exception cref="ParentServerException">The server could not be reached, rejected the credentials or answered unexpectedly.</exception>
    public async Task<AuthenticationResult> AuthenticateAsync(ParentEndpoint endpoint, string username, string password, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, endpoint, "Users/AuthenticateByName", null);
        request.Content = JsonContent.Create(new AuthenticateByNameRequest { Username = username, Pw = password }, options: JsonDefaults.Options);

        using var response = await SendAsync(request, _requestTimeout, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ParentServerException(ParentConnectionStatus.InvalidCredentials, "The parent server rejected the user name or password.");
        }

        EnsureSuccess(response);
        return await ReadJsonAsync<AuthenticationResult>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a request to the parent server with the authorization and custom headers applied.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="endpoint">The parent server.</param>
    /// <param name="relativePath">The path relative to the base URL, for example <c>System/Info/Public</c>.</param>
    /// <param name="accessToken">The access token, or null before sign in.</param>
    /// <returns>The request.</returns>
    public HttpRequestMessage CreateRequest(HttpMethod method, ParentEndpoint endpoint, string relativePath, string? accessToken)
    {
        var request = new HttpRequestMessage(method, new Uri(endpoint.BaseUrl, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", BuildAuthorizationParameter(accessToken));

        foreach (var header in endpoint.Headers)
        {
            if (string.IsNullOrWhiteSpace(header.Name))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Name.Trim(), header.Value ?? string.Empty))
            {
                _logger.LogWarning("Ignoring custom header {HeaderName} because it is not a valid request header", header.Name);
            }
        }

        return request;
    }

    /// <summary>
    /// Sends a request, translating transport failures and timeouts into <see cref="ParentServerException"/>.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="timeout">How long to wait for the response headers.</param>
    /// <param name="completionOption">Whether to wait for the whole body.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The response. The caller owns it.</returns>
    /// <exception cref="ParentServerException">The server could not be reached in time.</exception>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, TimeSpan timeout, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var host = request.RequestUri?.Host ?? "the parent server";
        try
        {
            return await _httpClientFactory.CreateClient(NamedClient.Default)
                .SendAsync(request, completionOption, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            var seconds = ((int)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            throw new ParentServerException(
                ParentConnectionStatus.ConnectionFailed,
                "Could not connect to " + host + ": no answer within " + seconds + " seconds.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ParentServerException(
                ParentConnectionStatus.ConnectionFailed,
                "Could not connect to " + host + ": " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Maps a non-success status code to a <see cref="ParentServerException"/>.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <exception cref="ParentServerException">The status code is not a success code.</exception>
    public static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var code = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
                throw new ParentServerException(
                    ParentConnectionStatus.AccessDenied,
                    "The parent server or a proxy in front of it denied access (HTTP " + code + "). If it sits behind Cloudflare Access or a similar gateway, add the required headers.");
            case HttpStatusCode.NotFound:
                throw new ParentServerException(
                    ParentConnectionStatus.InvalidResponse,
                    "The parent server answered 404 Not Found. Check the URL, including any base path.");
            default:
                throw new ParentServerException(
                    ParentConnectionStatus.InvalidResponse,
                    "The parent server answered with HTTP " + code + ".");
        }
    }

    /// <summary>
    /// Reads a JSON body, refusing anything that does not look like a Jellyfin answer.
    /// </summary>
    /// <typeparam name="T">The expected type.</typeparam>
    /// <param name="response">The response.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The deserialized body.</returns>
    /// <exception cref="ParentServerException">The body is not the expected JSON.</exception>
    public static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ParentServerException(
                ParentConnectionStatus.InvalidResponse,
                "The URL answered with " + (mediaType ?? "no content type") + " instead of JSON. Check that it points at a Jellyfin server and not at a login page or web site.");
        }

        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                throw new ParentServerException(ParentConnectionStatus.InvalidResponse, "The parent server sent an empty answer.");
            }

            return value;
        }
        catch (JsonException ex)
        {
            throw new ParentServerException(ParentConnectionStatus.InvalidResponse, "The parent server sent an answer that could not be read: " + ex.Message, ex);
        }
    }

    private async Task<T> GetJsonAsync<T>(ParentEndpoint endpoint, string relativePath, string? accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, endpoint, relativePath, accessToken);
        using var response = await SendAsync(request, _requestTimeout, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized && accessToken is not null)
        {
            throw new ParentServerException(ParentConnectionStatus.InvalidCredentials, "The parent server no longer accepts the stored access token.");
        }

        EnsureSuccess(response);
        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private string BuildAuthorizationParameter(string? accessToken)
    {
        var parts = new List<string>
        {
            "Client=\"" + Uri.EscapeDataString(ClientName) + "\"",
            "Device=\"" + Uri.EscapeDataString(_appHost.FriendlyName ?? Environment.MachineName) + "\"",
            "DeviceId=\"" + Uri.EscapeDataString(_appHost.SystemId) + "\"",
            "Version=\"" + Uri.EscapeDataString(_appHost.ApplicationVersionString) + "\""
        };

        if (!string.IsNullOrEmpty(accessToken))
        {
            parts.Add("Token=\"" + Uri.EscapeDataString(accessToken) + "\"");
        }

        return string.Join(", ", parts);
    }

    private sealed class AuthenticateByNameRequest
    {
        public string? Username { get; set; }

        public string? Pw { get; set; }
    }
}
