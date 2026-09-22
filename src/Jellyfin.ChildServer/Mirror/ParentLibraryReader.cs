using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.ChildServer;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.ChildServer.Mirror;

/// <summary>
/// Reads libraries, items, images and media files from the parent server.
/// </summary>
public class ParentLibraryReader
{
    private const int PageSize = 500;
    private const string ItemFields = "Path,MediaSources,MediaStreams,Overview,Genres,Studios,Tags,ProviderIds,SortName,OriginalTitle,Taglines,DateCreated,Etag,ParentId";

    private static readonly TimeSpan _listTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _imageTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _mediaTimeout = TimeSpan.FromSeconds(30);

    private readonly ParentServerClient _client;
    private readonly ILogger<ParentLibraryReader> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ParentLibraryReader"/> class.
    /// </summary>
    /// <param name="client">The parent server client.</param>
    /// <param name="logger">The logger.</param>
    public ParentLibraryReader(ParentServerClient client, ILogger<ParentLibraryReader> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Gets the libraries (views) the signed in parent user can see.
    /// </summary>
    /// <param name="session">The parent session.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The views.</returns>
    public async Task<IReadOnlyList<BaseItemDto>> GetViewsAsync(ParentSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var result = await GetJsonAsync<QueryResult<BaseItemDto>>(session, "UserViews?userId=" + Uri.EscapeDataString(session.UserId), _listTimeout, cancellationToken).ConfigureAwait(false);
        return result.Items ?? Array.Empty<BaseItemDto>();
    }

    /// <summary>
    /// Gets every item of the given kinds below a view, page by page.
    /// </summary>
    /// <param name="session">The parent session.</param>
    /// <param name="viewId">The view id.</param>
    /// <param name="kinds">The item kinds to include.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The items.</returns>
    public async Task<IReadOnlyList<BaseItemDto>> GetViewItemsAsync(ParentSession session, Guid viewId, IEnumerable<BaseItemKind> kinds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var includeTypes = string.Join(',', kinds.Select(k => k.ToString()));
        var items = new List<BaseItemDto>();
        var startIndex = 0;
        while (true)
        {
            var path = "Items?userId=" + Uri.EscapeDataString(session.UserId)
                + "&parentId=" + viewId.ToString("N", CultureInfo.InvariantCulture)
                + "&recursive=true&includeItemTypes=" + Uri.EscapeDataString(includeTypes)
                + "&fields=" + Uri.EscapeDataString(ItemFields)
                + "&sortBy=SortName&sortOrder=Ascending"
                + "&startIndex=" + startIndex.ToString(CultureInfo.InvariantCulture)
                + "&limit=" + PageSize.ToString(CultureInfo.InvariantCulture);

            var page = await GetJsonAsync<QueryResult<BaseItemDto>>(session, path, _listTimeout, cancellationToken).ConfigureAwait(false);
            var pageItems = page.Items ?? Array.Empty<BaseItemDto>();
            items.AddRange(pageItems);

            if (pageItems.Count < PageSize || items.Count >= page.TotalRecordCount)
            {
                break;
            }

            startIndex += pageItems.Count;
        }

        _logger.LogDebug("Read {Count} items of kinds {Kinds} from parent view {ViewId}", items.Count, includeTypes, viewId);
        return items;
    }

    /// <summary>
    /// Gets the direct children of a folder, in sort name order: sub folders, series, seasons and media items.
    /// </summary>
    /// <param name="session">The parent session.</param>
    /// <param name="parentId">The folder id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The children.</returns>
    public async Task<IReadOnlyList<BaseItemDto>> GetChildrenAsync(ParentSession session, Guid parentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var items = new List<BaseItemDto>();
        var startIndex = 0;
        while (true)
        {
            var path = "Items?userId=" + Uri.EscapeDataString(session.UserId)
                + "&parentId=" + parentId.ToString("N", CultureInfo.InvariantCulture)
                + "&fields=" + Uri.EscapeDataString(ItemFields)
                + "&sortBy=SortName&sortOrder=Ascending"
                + "&startIndex=" + startIndex.ToString(CultureInfo.InvariantCulture)
                + "&limit=" + PageSize.ToString(CultureInfo.InvariantCulture);

            var page = await GetJsonAsync<QueryResult<BaseItemDto>>(session, path, _listTimeout, cancellationToken).ConfigureAwait(false);
            var pageItems = page.Items ?? Array.Empty<BaseItemDto>();
            items.AddRange(pageItems);

            if (pageItems.Count < PageSize || items.Count >= page.TotalRecordCount)
            {
                break;
            }

            startIndex += pageItems.Count;
        }

        return items;
    }

    /// <summary>
    /// Downloads an image of an item, scaled down for the local sidecar.
    /// </summary>
    /// <param name="session">The parent session.</param>
    /// <param name="itemId">The parent item id.</param>
    /// <param name="imageType">The image type.</param>
    /// <param name="imageIndex">The image index, for backdrops.</param>
    /// <param name="maxWidth">The maximum width.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The image bytes and content type, or null when the item has no such image.</returns>
    public async Task<ParentImage?> GetImageAsync(ParentSession session, Guid itemId, ImageType imageType, int? imageIndex, int maxWidth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var path = "Items/" + itemId.ToString("N", CultureInfo.InvariantCulture) + "/Images/" + imageType.ToString();
        if (imageIndex.HasValue)
        {
            path += "/" + imageIndex.Value.ToString(CultureInfo.InvariantCulture);
        }

        path += "?maxWidth=" + maxWidth.ToString(CultureInfo.InvariantCulture) + "&quality=90";

        using var request = _client.CreateRequest(HttpMethod.Get, session.Endpoint, path, session.AccessToken);
        using var response = await _client.SendAsync(request, _imageTimeout, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        ThrowIfUnauthorized(response);
        ParentServerClient.EnsureSuccess(response);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new ParentImage(bytes, response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// Opens the media file of an item for download, optionally resuming at an offset.
    /// </summary>
    /// <param name="session">The parent session.</param>
    /// <param name="parentItemId">The parent item id.</param>
    /// <param name="mediaSourceId">The media source id, when known.</param>
    /// <param name="rangeStart">The first byte to fetch.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The response with only the headers read; the caller streams and disposes it.</returns>
    public async Task<HttpResponseMessage> OpenMediaAsync(ParentSession session, string parentItemId, string? mediaSourceId, long rangeStart, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var path = "Videos/" + Uri.EscapeDataString(parentItemId) + "/stream?static=true";
        if (!string.IsNullOrEmpty(mediaSourceId))
        {
            path += "&mediaSourceId=" + Uri.EscapeDataString(mediaSourceId);
        }

        using var request = _client.CreateRequest(HttpMethod.Get, session.Endpoint, path, session.AccessToken);
        if (rangeStart > 0)
        {
            request.Headers.Range = new RangeHeaderValue(rangeStart, null);
        }

        var response = await _client.SendAsync(request, _mediaTimeout, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnauthorized(response);
            ParentServerClient.EnsureSuccess(response);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static void ThrowIfUnauthorized(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ParentServerException(ParentConnectionStatus.InvalidCredentials, "The parent server no longer accepts the stored access token.");
        }
    }

    private async Task<T> GetJsonAsync<T>(ParentSession session, string relativePath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var request = _client.CreateRequest(HttpMethod.Get, session.Endpoint, relativePath, session.AccessToken);
        using var response = await _client.SendAsync(request, timeout, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        ThrowIfUnauthorized(response);
        ParentServerClient.EnsureSuccess(response);
        return await ParentServerClient.ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }
}
