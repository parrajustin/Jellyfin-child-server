using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Jellyfin.ChildServer.Mirror;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Jellyfin.ChildServer.Tests;

public class NfoWriterTests
{
    [Fact]
    public void Movie_WritesTheFieldsTheNfoReaderUnderstands()
    {
        var item = new BaseItemDto
        {
            Type = BaseItemKind.Movie,
            Name = "Sample & Movie",
            OriginalTitle = "Original",
            SortName = "sample movie",
            ProductionYear = 2018,
            PremiereDate = new DateTime(2018, 4, 15, 0, 0, 0, DateTimeKind.Utc),
            Overview = "A <short> plot.",
            Taglines = new[] { "Tag line" },
            OfficialRating = "PG-13",
            CommunityRating = 7.25f,
            Genres = new[] { "Drama", "Comedy" },
            Studios = new[] { new NameGuidPair { Name = "Studio A" } },
            Tags = new[] { "tag1" },
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "12345", ["Imdb"] = "tt0000001" },
            RunTimeTicks = TimeSpan.FromMinutes(95).Ticks
        };

        var xml = NfoWriter.Movie(item);
        var document = XDocument.Parse(xml);
        var root = document.Root!;

        Assert.Equal("movie", root.Name.LocalName);
        Assert.Equal("Sample & Movie", root.Element("title")!.Value);
        Assert.Equal("Original", root.Element("originaltitle")!.Value);
        Assert.Equal("sample movie", root.Element("sorttitle")!.Value);
        Assert.Equal("2018", root.Element("year")!.Value);
        Assert.Equal("2018-04-15", root.Element("premiered")!.Value);
        Assert.Equal("A <short> plot.", root.Element("plot")!.Value);
        Assert.Equal("Tag line", root.Element("tagline")!.Value);
        Assert.Equal("PG-13", root.Element("mpaa")!.Value);
        Assert.Equal("7.3", root.Element("rating")!.Value);
        Assert.Equal(new[] { "Drama", "Comedy" }, root.Elements("genre").Select(e => e.Value));
        Assert.Equal("Studio A", root.Element("studio")!.Value);
        Assert.Equal("tag1", root.Element("tag")!.Value);

        var uniqueIds = root.Elements("uniqueid").ToList();
        Assert.Equal(2, uniqueIds.Count);
        Assert.Equal("Tmdb", uniqueIds[0].Attribute("type")!.Value);
        Assert.Equal("true", uniqueIds[0].Attribute("default")!.Value);
        Assert.Equal("12345", uniqueIds[0].Value);
        Assert.Equal("false", uniqueIds[1].Attribute("default")!.Value);

        // Runtime is deliberately left out: the exact ticks come from the parent, not from a minute-rounded NFO field.
        Assert.Null(root.Element("runtime"));
    }

    [Fact]
    public void Episode_WritesSeasonAndEpisodeNumbers()
    {
        var item = new BaseItemDto
        {
            Type = BaseItemKind.Episode,
            Name = "In My Time of Dying",
            SeriesName = "Supernatural",
            ParentIndexNumber = 2,
            IndexNumber = 1,
            IndexNumberEnd = 2,
            PremiereDate = new DateTime(2006, 9, 28, 0, 0, 0, DateTimeKind.Utc)
        };

        var root = XDocument.Parse(NfoWriter.Episode(item)).Root!;

        Assert.Equal("episodedetails", root.Name.LocalName);
        Assert.Equal("In My Time of Dying", root.Element("title")!.Value);
        Assert.Equal("Supernatural", root.Element("showtitle")!.Value);
        Assert.Equal("2", root.Element("season")!.Value);
        Assert.Equal("1", root.Element("episode")!.Value);
        Assert.Equal("2", root.Element("episodenumberend")!.Value);
        Assert.Equal("2006-09-28", root.Element("aired")!.Value);
        Assert.Null(root.Element("premiered"));
    }

    [Fact]
    public void SeriesAndSeason_UseTheirOwnRootElements()
    {
        var series = XDocument.Parse(NfoWriter.Series(new BaseItemDto { Type = BaseItemKind.Series, Name = "Supernatural", ProductionYear = 2005 })).Root!;
        Assert.Equal("tvshow", series.Name.LocalName);
        Assert.Equal("Supernatural", series.Element("title")!.Value);
        Assert.Equal("2005", series.Element("year")!.Value);

        var season = XDocument.Parse(NfoWriter.Season(new BaseItemDto { Type = BaseItemKind.Season, Name = "Season 2", IndexNumber = 2 })).Root!;
        Assert.Equal("season", season.Name.LocalName);
        Assert.Equal("2", season.Element("seasonnumber")!.Value);
        Assert.Equal("Season 2", season.Element("title")!.Value);
    }

    [Fact]
    public void Output_IsUtf8WithoutByteOrderMarkAndEndsWithNewline()
    {
        var xml = NfoWriter.Movie(new BaseItemDto { Type = BaseItemKind.Movie, Name = "Ünïcödé" });

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"", xml, StringComparison.Ordinal);
        Assert.EndsWith(Environment.NewLine, xml, StringComparison.Ordinal);
        Assert.Contains("Ünïcödé", xml, StringComparison.Ordinal);
    }
}
