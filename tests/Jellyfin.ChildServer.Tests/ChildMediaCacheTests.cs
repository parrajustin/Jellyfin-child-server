using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.ChildServer.Cache;
using Jellyfin.ChildServer.Mirror;
using MediaBrowser.Controller;
using MediaBrowser.Controller.ChildServer;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.ChildServer;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.ChildServer.Tests;

public sealed class ChildMediaCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jellyfin-child-tests", Guid.NewGuid().ToString("N"));
    private readonly StubHttpMessageHandler _handler = new();
    private readonly ChildServerConfiguration _config = new()
    {
        ParentUrl = "http://parent.example:8096/",
        Username = "alice",
        Password = "secret",
        AccessToken = "token-0",
        ParentUserId = "user-1",
        MaxCacheSizeMb = 0
    };

    private readonly MirrorManifest _manifest;
    private readonly ChildMediaCache _cache;
    private readonly byte[] _payload = MakePayload(300_000);
    private readonly string _mediaPath;

    public ChildMediaCacheTests()
    {
        Directory.CreateDirectory(_directory);
        _mediaPath = Path.Combine(_directory, "Movies", "Sample (2018)", "Sample (2018).avi");
        Directory.CreateDirectory(Path.GetDirectoryName(_mediaPath)!);
        File.WriteAllBytes(_mediaPath, Array.Empty<byte>());

        _manifest = new MirrorManifest(() => Path.Combine(_directory, "mirror.json"), NullLogger<MirrorManifest>.Instance);
        _manifest.Upsert(new MirrorEntry
        {
            Path = _mediaPath,
            View = "Movies",
            Name = "Sample",
            Kind = "Movie",
            ParentItemId = "p1",
            ParentMediaSourceId = "ms1",
            ExpectedSize = _payload.Length,
            Container = "avi"
        });

        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(c => c.GetConfiguration(ChildServerManager.ConfigurationKey)).Returns(_config);
        var appHost = new Mock<IServerApplicationHost>();
        appHost.SetupGet(h => h.SystemId).Returns("child");
        appHost.SetupGet(h => h.FriendlyName).Returns("Child");
        appHost.SetupGet(h => h.ApplicationVersionString).Returns("12.1.0");

        var client = new ParentServerClient(new StubHttpClientFactory(_handler), appHost.Object, NullLogger<ParentServerClient>.Instance);
        var manager = new ChildServerManager(configurationManager.Object, client, NullLogger<ChildServerManager>.Instance);
        var reader = new ParentLibraryReader(client, NullLogger<ParentLibraryReader>.Instance);
        _cache = new ChildMediaCache(manager, client, reader, _manifest, NullLogger<ChildMediaCache>.Instance)
        {
            RetryBaseDelay = TimeSpan.Zero
        };
    }

    public void Dispose()
    {
        _cache.Dispose();
        _handler.Dispose();
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
    public void UnmanagedPaths_AreLeftAlone()
    {
        var other = Path.Combine(_directory, "other.mkv");

        Assert.False(_cache.IsManagedPath(other));
        Assert.Equal(ChildMediaCacheState.Unmanaged, _cache.GetState(other));
        Assert.Null(_cache.GetMediaInfo(other));
        Assert.Null(_cache.OpenProgressiveStream(other));
        Assert.True(_cache.IsManagedPath(_mediaPath));
    }

    [Fact]
    public void GetMediaInfo_ComesFromTheManifest()
    {
        var info = _cache.GetMediaInfo(_mediaPath);

        Assert.NotNull(info);
        Assert.Equal("p1", info.ParentItemId);
        Assert.Equal("ms1", info.ParentMediaSourceId);
        Assert.Equal("avi", info.Container);
        Assert.Equal(_payload.Length, info.Size);
    }

    [Fact]
    public async Task EnsureCached_DownloadsTheFileFromTheParent()
    {
        MapMedia();
        var before = _cache.GetState(_mediaPath);
        Assert.True(before.IsManaged);
        Assert.False(before.IsCached);

        await _cache.EnsureCachedAsync(_mediaPath, ChildFetchPriority.Playback, TestContext.Current.CancellationToken);

        Assert.Equal(_payload, await File.ReadAllBytesAsync(_mediaPath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(_mediaPath + ".jfpart"));
        var after = _cache.GetState(_mediaPath);
        Assert.True(after.IsCached);
        Assert.False(after.IsDownloading);
        Assert.Equal(_payload.Length, after.DownloadedBytes);
        Assert.True(_manifest.Get(_mediaPath)!.IsCached);
        Assert.NotNull(_manifest.Get(_mediaPath)!.LastAccessUtc);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("/Videos/p1/stream", request.RequestUri!.AbsolutePath);
        Assert.Contains("static=true", request.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("mediaSourceId=ms1", request.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("Token=\"token-0\"", request.Headers.Authorization!.Parameter, StringComparison.Ordinal);

        var statistics = _cache.GetStatistics();
        Assert.Equal(1, statistics.MirroredItemCount);
        Assert.Equal(1, statistics.CachedItemCount);
        Assert.Equal(_payload.Length, statistics.CachedBytes);
        Assert.Equal(0, statistics.ActiveDownloads);

        // A second call finds the file and does not download again.
        await _cache.EnsureCachedAsync(_mediaPath, ChildFetchPriority.Playback, TestContext.Current.CancellationToken);
        Assert.Single(_handler.Requests);
        Assert.Null(_cache.OpenProgressiveStream(_mediaPath));
    }

    [Fact]
    public async Task ProgressiveStream_ServesTheFileWhileItDownloads()
    {
        MapMedia(TimeSpan.FromMilliseconds(25));

        byte[] read;
        using (var stream = _cache.OpenProgressiveStream(_mediaPath))
        {
            Assert.NotNull(stream);
            Assert.Equal(_payload.Length, stream.Length);
            Assert.True(_cache.GetState(_mediaPath).IsDownloading);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, TestContext.Current.CancellationToken);
            read = memory.ToArray();
        }

        Assert.Equal(_payload, read);
        await _cache.EnsureCachedAsync(_mediaPath, ChildFetchPriority.Playback, TestContext.Current.CancellationToken);
        var state = _cache.GetState(_mediaPath);
        Assert.True(
            state.IsCached,
            "state " + state + ", file " + new FileInfo(_mediaPath).Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ", part exists " + File.Exists(_mediaPath + ".jfpart") + ", manifest cached " + _manifest.Get(_mediaPath)!.IsCached);
    }

    [Fact]
    public async Task EnsureCached_ResumesAPartialDownload()
    {
        MapMedia();
        await File.WriteAllBytesAsync(_mediaPath + ".jfpart", _payload.AsSpan(0, 100_000).ToArray(), TestContext.Current.CancellationToken);

        await _cache.EnsureCachedAsync(_mediaPath, ChildFetchPriority.Prefetch, TestContext.Current.CancellationToken);

        var request = Assert.Single(_handler.Requests);
        Assert.NotNull(request.Headers.Range);
        Assert.Equal(100_000, request.Headers.Range!.Ranges.Single().From);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(_mediaPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsureCached_ParentUnreachable_ThrowsAndLeavesThePlaceholder()
    {
        _handler.FailWith = new HttpRequestException("Connection refused");

        await Assert.ThrowsAsync<ParentServerUnavailableException>(() => _cache.EnsureCachedAsync(_mediaPath, ChildFetchPriority.Playback, TestContext.Current.CancellationToken));

        Assert.Equal(3, _handler.Requests.Count);
        Assert.Equal(0, new FileInfo(_mediaPath).Length);
        Assert.False(_cache.GetState(_mediaPath).IsCached);
        Assert.False(_cache.GetState(_mediaPath).IsDownloading);
    }

    [Fact]
    public async Task PrepareForStreaming_WaitsForTranscodesButNotForStaticStreams()
    {
        MapMedia(TimeSpan.FromMilliseconds(2));
        var mediaSource = new MediaSourceInfo { Path = _mediaPath };

        await _cache.PrepareForStreamingAsync(null!, mediaSource, true, TestContext.Current.CancellationToken);
        Assert.True(_cache.GetState(_mediaPath).IsDownloading);

        await _cache.PrepareForStreamingAsync(null!, mediaSource, false, TestContext.Current.CancellationToken);
        Assert.True(_cache.GetState(_mediaPath).IsCached);
    }

    [Fact]
    public async Task IsParentReachable_ReflectsThePublicInfoEndpoint()
    {
        _handler.MapJson("/System/Info/Public", HttpStatusCode.OK, "{\"ServerName\":\"Parent\",\"Version\":\"12.1.0\",\"Id\":\"p\"}");
        Assert.True(await _cache.IsParentReachableAsync(TestContext.Current.CancellationToken));

        _handler.FailWith = new HttpRequestException("Connection refused");
        _cache.ResetReachability();
        Assert.False(await _cache.IsParentReachableAsync(TestContext.Current.CancellationToken));

        // The negative answer is cached briefly.
        _handler.FailWith = null;
        Assert.False(await _cache.IsParentReachableAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Eviction_TruncatesTheLeastRecentlyUsedFiles()
    {
        _config.MaxCacheSizeMb = 1;
        // 0.8 + 0.8 + 0.3 MB against a 1 MiB limit: both old files must go, the fresh download stays.
        var old1 = AddCachedEntry("old1.avi", 800_000, DateTime.UtcNow.AddHours(-2));
        var old2 = AddCachedEntry("old2.avi", 800_000, DateTime.UtcNow.AddHours(-1));
        MapMedia();

        await _cache.EnsureCachedAsync(_mediaPath, ChildFetchPriority.Playback, TestContext.Current.CancellationToken);

        Assert.Equal(0, new FileInfo(old1).Length);
        Assert.Equal(0, new FileInfo(old2).Length);
        Assert.False(_manifest.Get(old1)!.IsCached);
        Assert.Equal(_payload.Length, new FileInfo(_mediaPath).Length);
        Assert.True(_cache.GetState(_mediaPath).IsCached);
    }

    private static byte[] MakePayload(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < length; i++)
        {
            payload[i] = (byte)((i * 17) % 253);
        }

        return payload;
    }

    private string AddCachedEntry(string fileName, int size, DateTime lastAccess)
    {
        var path = Path.Combine(_directory, "Movies", fileName);
        File.WriteAllBytes(path, MakePayload(size));
        _manifest.Upsert(new MirrorEntry
        {
            Path = path,
            View = "Movies",
            Name = fileName,
            ParentItemId = fileName,
            ExpectedSize = size,
            IsCached = true,
            LastAccessUtc = lastAccess
        });
        return path;
    }

    private void MapMedia(TimeSpan? chunkDelay = null)
    {
        _handler.Map("/Videos/p1/stream", request =>
        {
            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            var slice = _payload.AsSpan((int)from).ToArray();
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new SlowStreamContent(slice, 20_000, chunkDelay ?? TimeSpan.Zero)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/x-msvideo");
            if (from > 0)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, _payload.Length - 1, _payload.Length);
            }

            return response;
        });
    }

    /// <summary>
    /// Serves bytes in small chunks with a delay between them, so callers see a download in progress.
    /// </summary>
    private sealed class SlowStreamContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly int _chunkSize;
        private readonly TimeSpan _delay;

        public SlowStreamContent(byte[] bytes, int chunkSize, TimeSpan delay)
        {
            _bytes = bytes;
            _chunkSize = chunkSize;
            _delay = delay;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new SlowReadStream(_bytes, _chunkSize, _delay));

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var offset = 0;
            while (offset < _bytes.Length)
            {
                var count = Math.Min(_chunkSize, _bytes.Length - offset);
                await stream.WriteAsync(_bytes.AsMemory(offset, count));
                offset += count;
                if (_delay > TimeSpan.Zero)
                {
                    await Task.Delay(_delay);
                }
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }
    }

    private sealed class SlowReadStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly int _chunkSize;
        private readonly TimeSpan _delay;
        private int _position;

        public SlowReadStream(byte[] bytes, int chunkSize, TimeSpan delay)
        {
            _bytes = bytes;
            _chunkSize = chunkSize;
            _delay = delay;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _bytes.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _bytes.Length)
            {
                return 0;
            }

            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            var count = Math.Min(Math.Min(_chunkSize, buffer.Length), _bytes.Length - _position);
            _bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
