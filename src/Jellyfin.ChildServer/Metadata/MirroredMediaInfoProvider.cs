using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.ChildServer;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.ChildServer.Metadata;

/// <summary>
/// Fills in the media information of mirrored placeholders from what the parent server reported,
/// so the library scan never has to probe an empty file. Runs before the probe provider, which skips mirrored files.
/// </summary>
public class MirroredMediaInfoProvider : ICustomMetadataProvider<Movie>,
    ICustomMetadataProvider<Episode>,
    ICustomMetadataProvider<Video>,
    IHasOrder,
    IForcedProvider,
    IPreRefreshProvider,
    IHasItemChangeMonitor
{
    private readonly IChildServerMediaCache _cache;
    private readonly IMediaStreamRepository _mediaStreamRepository;
    private readonly ILogger<MirroredMediaInfoProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MirroredMediaInfoProvider"/> class.
    /// </summary>
    /// <param name="cache">The child server media cache.</param>
    /// <param name="mediaStreamRepository">The media stream repository.</param>
    /// <param name="logger">The logger.</param>
    public MirroredMediaInfoProvider(IChildServerMediaCache cache, IMediaStreamRepository mediaStreamRepository, ILogger<MirroredMediaInfoProvider> logger)
    {
        _cache = cache;
        _mediaStreamRepository = mediaStreamRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Child Server Mirror";

    /// <inheritdoc />
    public int Order => 90;

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Movie item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        => ApplyAsync(item, cancellationToken);

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        => ApplyAsync(item, cancellationToken);

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Video item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        => ApplyAsync(item, cancellationToken);

    /// <inheritdoc />
    public bool HasChanged(BaseItem item, IDirectoryService directoryService)
    {
        var info = _cache.GetMediaInfo(item.Path);
        if (info is null)
        {
            return false;
        }

        return item.RunTimeTicks != info.RunTimeTicks
            || item.Size != info.Size
            || !string.Equals(item.Container, info.Container, StringComparison.Ordinal);
    }

    private Task<ItemUpdateType> ApplyAsync(Video item, CancellationToken cancellationToken)
    {
        var info = _cache.GetMediaInfo(item.Path);
        if (info is null)
        {
            return Task.FromResult(ItemUpdateType.None);
        }

        item.Container = info.Container;
        item.RunTimeTicks = info.RunTimeTicks;
        item.TotalBitrate = info.Bitrate;
        item.Size = info.Size;

        if (info.MediaStreams.Count > 0)
        {
            _mediaStreamRepository.SaveMediaStreams(item.Id, info.MediaStreams, cancellationToken);
            var videoStream = info.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (videoStream is not null)
            {
                item.DefaultVideoStreamIndex = videoStream.Index;
            }
        }

        _logger.LogDebug("Applied parent media information to mirrored item {Name}", item.Name);
        return Task.FromResult(ItemUpdateType.MetadataImport);
    }
}
