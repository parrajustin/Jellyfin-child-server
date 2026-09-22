using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.ChildServer.Cache;

/// <summary>
/// Decides which episodes to download ahead while one is being watched.
/// </summary>
public static class PrefetchPlanner
{
    /// <summary>
    /// Picks up to <paramref name="count"/> episodes that follow <paramref name="current"/> in season and episode order.
    /// Specials and episodes without numbers are never picked.
    /// </summary>
    /// <param name="current">The episode being watched.</param>
    /// <param name="candidates">Every episode of the series.</param>
    /// <param name="count">How many to pick.</param>
    /// <returns>The following episodes, nearest first.</returns>
    public static IReadOnlyList<Episode> SelectFollowing(Episode current, IEnumerable<Episode> candidates, int count)
    {
        if (count <= 0 || current.ParentIndexNumber is null || current.IndexNumber is null)
        {
            return [];
        }

        var position = (Season: current.ParentIndexNumber.Value, Episode: current.IndexNumber.Value);
        return candidates
            .Where(e => e.ParentIndexNumber is > 0 && e.IndexNumber is not null && !e.Id.Equals(current.Id))
            .Select(e => (Episode: e, Position: (Season: e.ParentIndexNumber!.Value, Episode: e.IndexNumber!.Value)))
            .Where(e => e.Position.CompareTo(position) > 0)
            .OrderBy(e => e.Position.Season)
            .ThenBy(e => e.Position.Episode)
            .Select(e => e.Episode)
            .Take(count)
            .ToList();
    }
}
