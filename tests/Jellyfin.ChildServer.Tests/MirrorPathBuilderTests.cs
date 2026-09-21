using System;
using Jellyfin.ChildServer.Mirror;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Jellyfin.ChildServer.Tests;

public class MirrorPathBuilderTests
{
    [Theory]
    [InlineData("Supernatural", "Supernatural")]
    [InlineData("  Mission: Impossible  ", "Mission Impossible")]
    [InlineData("What/If\\Not?", "What If Not")]
    [InlineData("Ends with dots...", "Ends with dots")]
    [InlineData("", "Untitled")]
    [InlineData(null, "Untitled")]
    public void SanitizeName_RemovesInvalidCharacters(string? input, string expected)
    {
        Assert.Equal(expected, MirrorPathBuilder.SanitizeName(input));
    }

    [Fact]
    public void SanitizeName_TruncatesVeryLongNames()
    {
        var name = new string('a', 500);

        Assert.Equal(120, MirrorPathBuilder.SanitizeName(name).Length);
    }

    [Fact]
    public void GetExtension_PrefersTheParentsFileExtension()
    {
        var item = new BaseItemDto { Path = "/media/Movies/Sample.mkv", Container = "avi" };

        Assert.Equal("mkv", MirrorPathBuilder.GetExtension(item));
    }

    [Theory]
    [InlineData("avi", "avi")]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "mp4")]
    [InlineData("matroska,webm", "mkv")]
    [InlineData("mpegts", "ts")]
    [InlineData(null, "mkv")]
    public void GetExtension_FallsBackToTheContainer(string? container, string expected)
    {
        var item = new BaseItemDto { Container = container };

        Assert.Equal(expected, MirrorPathBuilder.GetExtension(item));
    }

    [Fact]
    public void GetExtension_UsesTheMediaSourceContainerWhenTheItemHasNone()
    {
        var item = new BaseItemDto { MediaSources = new[] { new MediaSourceInfo { Container = "webm" } } };

        Assert.Equal("webm", MirrorPathBuilder.GetExtension(item));
    }

    [Fact]
    public void TitledFolderName_AppendsTheYear()
    {
        Assert.Equal("Sample Movie (2018)", MirrorPathBuilder.TitledFolderName(new BaseItemDto { Name = "Sample Movie", ProductionYear = 2018 }));
        Assert.Equal("Sample Movie", MirrorPathBuilder.TitledFolderName(new BaseItemDto { Name = "Sample Movie" }));
        Assert.Equal("Sample Movie (2018)", MirrorPathBuilder.TitledFolderName(new BaseItemDto { Name = "Sample Movie (2018)", ProductionYear = 2018 }));
    }

    [Theory]
    [InlineData(0, null, "Specials")]
    [InlineData(2, null, "Season 02")]
    [InlineData(12, "Whatever", "Season 12")]
    [InlineData(null, "Extras", "Extras")]
    [InlineData(null, null, "Season 01")]
    public void SeasonFolderName_FollowsJellyfinConventions(int? number, string? name, string expected)
    {
        Assert.Equal(expected, MirrorPathBuilder.SeasonFolderName(number, name));
    }

    [Fact]
    public void EpisodeFileName_UsesSeasonAndEpisodeNumbers()
    {
        var episode = new BaseItemDto { Type = BaseItemKind.Episode, Name = "In My Time of Dying", ParentIndexNumber = 2, IndexNumber = 1 };

        Assert.Equal("Supernatural S02E01", MirrorPathBuilder.EpisodeFileName("Supernatural", episode));
    }

    [Fact]
    public void EpisodeFileName_MarksMultiEpisodeFiles()
    {
        var episode = new BaseItemDto { Type = BaseItemKind.Episode, ParentIndexNumber = 1, IndexNumber = 1, IndexNumberEnd = 2 };

        Assert.Equal("Show S01E01-E02", MirrorPathBuilder.EpisodeFileName("Show", episode));
    }

    [Fact]
    public void EpisodeFileName_FallsBackToTheEpisodeName()
    {
        var episode = new BaseItemDto { Type = BaseItemKind.Episode, Name = "Pilot" };

        Assert.Equal("Show - Pilot", MirrorPathBuilder.EpisodeFileName("Show", episode));
    }

    [Theory]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("image/png", "png")]
    [InlineData("image/webp", "webp")]
    [InlineData(null, "jpg")]
    public void ImageExtension_MapsContentTypes(string? contentType, string expected)
    {
        Assert.Equal(expected, MirrorPathBuilder.ImageExtension(contentType));
    }
}
