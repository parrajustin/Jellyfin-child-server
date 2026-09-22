using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.ChildServer.Cache;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.ChildServer.Tests;

public class PrefetchPlannerTests
{
    [Fact]
    public void SelectFollowing_PicksTheNextEpisodesInOrderAcrossSeasons()
    {
        var all = Series((1, 1), (1, 2), (2, 1), (2, 2), (2, 3), (2, 4), (2, 5), (2, 6), (2, 7), (3, 1));
        var current = all.Single(e => e.ParentIndexNumber == 2 && e.IndexNumber == 5);

        var following = PrefetchPlanner.SelectFollowing(current, Shuffle(all), 4);

        Assert.Equal(new[] { "S02E06", "S02E07", "S03E01" }, following.Select(Key));
    }

    [Fact]
    public void SelectFollowing_LimitsTheCount()
    {
        var all = Series((2, 1), (2, 2), (2, 3), (2, 4), (2, 5), (2, 6), (2, 7));
        var current = all.First();

        var following = PrefetchPlanner.SelectFollowing(current, all, 4);

        Assert.Equal(new[] { "S02E02", "S02E03", "S02E04", "S02E05" }, following.Select(Key));
    }

    [Fact]
    public void SelectFollowing_IgnoresEarlierEpisodesSpecialsAndUnnumberedItems()
    {
        var all = Series((0, 1), (1, 1), (1, 2), (2, 1), (2, 2)).ToList();
        all.Add(new Episode { Id = Guid.NewGuid(), Name = "unnumbered" });
        var current = all.Single(e => e.ParentIndexNumber == 1 && e.IndexNumber == 2);

        var following = PrefetchPlanner.SelectFollowing(current, all, 10);

        Assert.Equal(new[] { "S02E01", "S02E02" }, following.Select(Key));
    }

    [Fact]
    public void SelectFollowing_NothingWhenTheCountIsZeroOrTheCurrentEpisodeHasNoNumbers()
    {
        var all = Series((1, 1), (1, 2));

        Assert.Empty(PrefetchPlanner.SelectFollowing(all[0], all, 0));
        Assert.Empty(PrefetchPlanner.SelectFollowing(new Episode { Id = Guid.NewGuid() }, all, 4));
    }

    private static string Key(Episode episode)
        => "S" + episode.ParentIndexNumber!.Value.ToString("00", System.Globalization.CultureInfo.InvariantCulture)
            + "E" + episode.IndexNumber!.Value.ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    private static List<Episode> Series(params (int Season, int Episode)[] numbers)
        => numbers.Select(n => new Episode
        {
            Id = Guid.NewGuid(),
            Name = "Episode " + n.Episode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ParentIndexNumber = n.Season,
            IndexNumber = n.Episode
        }).ToList();

    private static IEnumerable<Episode> Shuffle(IEnumerable<Episode> episodes)
        => episodes.OrderByDescending(e => e.IndexNumber).ThenBy(e => e.ParentIndexNumber);
}
