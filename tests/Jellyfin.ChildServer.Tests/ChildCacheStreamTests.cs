using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.ChildServer.Cache;
using Jellyfin.ChildServer.Mirror;
using MediaBrowser.Controller.ChildServer;
using Xunit;

namespace Jellyfin.ChildServer.Tests;

public sealed class ChildCacheStreamTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jellyfin-child-tests", Guid.NewGuid().ToString("N"));
    private readonly string _finalPath;
    private readonly string _partPath;

    public ChildCacheStreamTests()
    {
        Directory.CreateDirectory(_directory);
        _finalPath = Path.Combine(_directory, "video.avi");
        _partPath = _finalPath + ".jfpart";
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
    public async Task ReadsEverythingWhileTheFileIsStillBeingWritten()
    {
        var payload = MakePayload(100_000);
        var job = CreateJob(payload.Length);
        var writer = WriteSlowlyAsync(job, payload, 7_000, TimeSpan.FromMilliseconds(5));

        byte[] read;
        using (var stream = new ChildCacheStream(job))
        {
            Assert.Equal(payload.Length, stream.Length);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, TestContext.Current.CancellationToken);
            read = memory.ToArray();
        }

        await writer;
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task SeekingAheadWaitsForTheBytesToArrive()
    {
        var payload = MakePayload(50_000);
        var job = CreateJob(payload.Length);
        var writer = WriteSlowlyAsync(job, payload, 5_000, TimeSpan.FromMilliseconds(5));

        using var stream = new ChildCacheStream(job);
        stream.Seek(45_000, SeekOrigin.Begin);
        var buffer = new byte[5_000];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), TestContext.Current.CancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        await writer;
        Assert.Equal(5_000, total);
        Assert.Equal(payload.AsSpan(45_000, 5_000).ToArray(), buffer);
        Assert.Equal(0, await ReadFullyAsync(stream, new byte[10]));
    }

    [Fact]
    public async Task AFailedDownloadSurfacesAsAnIoException()
    {
        var job = CreateJob(10_000);
        await File.WriteAllBytesAsync(_partPath, MakePayload(1_000), TestContext.Current.CancellationToken);
        job.ReportProgress(1_000);

        using var stream = new ChildCacheStream(job);
        var first = new byte[1_000];
        Assert.Equal(1_000, await ReadFullyAsync(stream, first));

        job.Fail(new InvalidOperationException("parent went away"));

        await Assert.ThrowsAsync<IOException>(async () => await ReadFullyAsync(stream, new byte[100]));
    }

    [Fact]
    public async Task DisposeCallbackRunsOnce()
    {
        var job = CreateJob(10);
        await File.WriteAllBytesAsync(_finalPath, MakePayload(10), TestContext.Current.CancellationToken);
        job.Complete(10);
        var calls = 0;

        var stream = new ChildCacheStream(job, () => calls++);
        var buffer = new byte[10];
        Assert.Equal(10, await ReadFullyAsync(stream, buffer));
        await stream.DisposeAsync();
        await stream.DisposeAsync();

        Assert.Equal(1, calls);
        Assert.Equal(MakePayload(10), buffer);
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), TestContext.Current.CancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static byte[] MakePayload(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < length; i++)
        {
            payload[i] = (byte)((i * 31) % 251);
        }

        return payload;
    }

    private DownloadJob CreateJob(long expectedSize)
    {
        var entry = new MirrorEntry { Path = _finalPath, Name = "video", ExpectedSize = expectedSize };
        return new DownloadJob(entry, _partPath, ChildFetchPriority.Playback);
    }

    private async Task WriteSlowlyAsync(DownloadJob job, byte[] payload, int chunkSize, TimeSpan delay)
    {
        await Task.Yield();
        var written = 0;
        var file = new FileStream(_partPath, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete);
        await using (file.ConfigureAwait(false))
        {
            while (written < payload.Length)
            {
                var count = Math.Min(chunkSize, payload.Length - written);
                await file.WriteAsync(payload.AsMemory(written, count));
                await file.FlushAsync();
                written += count;
                job.ReportProgress(written, payload.Length);
                await Task.Delay(delay);
            }
        }

        File.Move(_partPath, _finalPath, overwrite: true);
        job.Complete(payload.Length);
    }
}
