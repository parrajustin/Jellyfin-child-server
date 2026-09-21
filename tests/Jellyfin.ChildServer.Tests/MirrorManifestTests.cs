using System;
using System.IO;
using Jellyfin.ChildServer.Mirror;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.ChildServer.Tests;

public sealed class MirrorManifestTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jellyfin-child-tests", Guid.NewGuid().ToString("N"));

    public MirrorManifestTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public void Upsert_Get_Remove_RoundTrip()
    {
        var manifest = CreateManifest();
        var mediaPath = Path.Combine(_directory, "Movies", "Sample (2018)", "Sample (2018).avi");

        manifest.Upsert(new MirrorEntry { Path = mediaPath, Name = "Sample", ParentItemId = "abc", ExpectedSize = 42 });

        Assert.Equal(1, manifest.Count);
        var entry = manifest.Get(mediaPath);
        Assert.NotNull(entry);
        Assert.Equal("Sample", entry.Name);
        Assert.Equal(42, entry.ExpectedSize);

        // Lookups tolerate different separators and trailing slashes.
        Assert.NotNull(manifest.Get(mediaPath.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        Assert.Null(manifest.Get(Path.Combine(_directory, "other.avi")));
        Assert.Null(manifest.Get(null));
        Assert.Null(manifest.Get(string.Empty));

        Assert.True(manifest.Remove(mediaPath));
        Assert.False(manifest.Remove(mediaPath));
        Assert.Equal(0, manifest.Count);
    }

    [Fact]
    public void Save_PersistsEntriesIncludingMediaStreams()
    {
        var manifestPath = Path.Combine(_directory, "mirror.json");
        var manifest = new MirrorManifest(() => manifestPath, NullLogger<MirrorManifest>.Instance);
        var mediaPath = Path.Combine(_directory, "TV Shows", "Show", "Season 01", "Show S01E01.mkv");
        manifest.Upsert(new MirrorEntry
        {
            Path = mediaPath,
            View = "TV Shows",
            Name = "Pilot",
            ParentItemId = "p1",
            ParentMediaSourceId = "ms1",
            Kind = "Episode",
            ExpectedSize = 1234,
            Container = "mkv",
            RunTimeTicks = 600_000_000,
            Bitrate = 800_000,
            IsCached = true,
            LastAccessUtc = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc),
            MediaStreams = new[]
            {
                new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Index = 0, Width = 1280, Height = 720 },
                new MediaStream { Type = MediaStreamType.Audio, Codec = "aac", Index = 1, Channels = 2, Language = "eng" }
            }
        });

        manifest.Save();

        Assert.True(File.Exists(manifestPath));
        Assert.False(File.Exists(manifestPath + ".tmp"));

        var reloaded = new MirrorManifest(() => manifestPath, NullLogger<MirrorManifest>.Instance);
        var entry = reloaded.Get(mediaPath);
        Assert.NotNull(entry);
        Assert.Equal("Pilot", entry.Name);
        Assert.Equal("ms1", entry.ParentMediaSourceId);
        Assert.Equal(1234, entry.ExpectedSize);
        Assert.Equal("mkv", entry.Container);
        Assert.Equal(600_000_000, entry.RunTimeTicks);
        Assert.Equal(800_000, entry.Bitrate);
        Assert.True(entry.IsCached);
        Assert.Equal(new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc), entry.LastAccessUtc);
        Assert.Equal(2, entry.MediaStreams.Count);
        Assert.Equal("h264", entry.MediaStreams[0].Codec);
        Assert.Equal(MediaStreamType.Audio, entry.MediaStreams[1].Type);
        Assert.Equal("eng", entry.MediaStreams[1].Language);
    }

    [Fact]
    public void Load_IgnoresACorruptFile()
    {
        var manifestPath = Path.Combine(_directory, "mirror.json");
        File.WriteAllText(manifestPath, "{ not json");

        var manifest = new MirrorManifest(() => manifestPath, NullLogger<MirrorManifest>.Instance);

        Assert.Equal(0, manifest.Count);
    }

    private MirrorManifest CreateManifest()
        => new(() => Path.Combine(_directory, "mirror.json"), NullLogger<MirrorManifest>.Instance);
}
