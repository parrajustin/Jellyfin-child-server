using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.ChildServer;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.ChildServer.Cache;

/// <summary>
/// When someone starts watching a mirrored episode, downloads the episodes that are likely to follow.
/// </summary>
public class ChildPrefetchConsumer : IEventConsumer<PlaybackStartEventArgs>
{
    private readonly IChildServerManager _manager;
    private readonly IChildServerMediaCache _cache;
    private readonly ILogger<ChildPrefetchConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildPrefetchConsumer"/> class.
    /// </summary>
    /// <param name="manager">The child server manager, for the prefetch count.</param>
    /// <param name="cache">The media cache.</param>
    /// <param name="logger">The logger.</param>
    public ChildPrefetchConsumer(IChildServerManager manager, IChildServerMediaCache cache, ILogger<ChildPrefetchConsumer> logger)
    {
        _manager = manager;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task OnEvent(PlaybackStartEventArgs eventArgs)
    {
        if (eventArgs.Item is not Episode episode || !_cache.IsManagedPath(episode.Path))
        {
            return Task.CompletedTask;
        }

        var count = _manager.Configuration.PrefetchEpisodeCount;
        if (count <= 0)
        {
            return Task.CompletedTask;
        }

        var series = episode.Series;
        if (series is null)
        {
            return Task.CompletedTask;
        }

        var user = eventArgs.Users.Count > 0 ? eventArgs.Users[0] : null;
        var candidates = series.GetEpisodes(user, new DtoOptions(false), false).OfType<Episode>();
        var following = PrefetchPlanner.SelectFollowing(episode, candidates, count);
        foreach (var next in following)
        {
            if (!_cache.IsManagedPath(next.Path))
            {
                continue;
            }

            _logger.LogInformation("Prefetching {Name} because {Current} started playing", next.Name, episode.Name);
            _ = PrefetchAsync(next);
        }

        return Task.CompletedTask;
    }

    private async Task PrefetchAsync(Episode episode)
    {
        try
        {
            await _cache.EnsureCachedAsync(episode.Path, ChildFetchPriority.Prefetch, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prefetching {Name} failed", episode.Name);
        }
    }
}
