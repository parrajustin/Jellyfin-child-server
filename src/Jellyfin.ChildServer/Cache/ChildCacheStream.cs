using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.ChildServer.Cache;

/// <summary>
/// A seekable read stream over a file that is still being downloaded from the parent server.
/// Reads block until the requested bytes have arrived, so HTTP range requests work while the download runs.
/// </summary>
public sealed class ChildCacheStream : Stream
{
    private static readonly TimeSpan _progressWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _stallTimeout = TimeSpan.FromMinutes(5);

    private readonly DownloadJob _job;
    private readonly Action? _onDispose;
    private FileStream? _file;
    private long _position;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChildCacheStream"/> class.
    /// </summary>
    /// <param name="job">The download to read from.</param>
    /// <param name="onDispose">Called once when the stream is disposed.</param>
    public ChildCacheStream(DownloadJob job, Action? onDispose = null)
    {
        _job = job;
        _onDispose = onDispose;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _job.ExpectedBytes > 0 ? _job.ExpectedBytes : _job.DownloadedBytes;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0)
        {
            return 0;
        }

        var stalledSince = DateTime.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var expected = _job.ExpectedBytes;
            var available = _job.DownloadedBytes;
            if ((expected > 0 && _position >= expected) || (_job.IsCompleted && _position >= available))
            {
                return 0;
            }

            if (_position < available)
            {
                var toRead = (int)Math.Min(buffer.Length, available - _position);
                var read = await ReadFromFileAsync(buffer.Slice(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read > 0)
                {
                    _position += read;
                    return read;
                }
            }

            if (_job.Error is not null)
            {
                throw new IOException("The download from the parent server failed.", _job.Error);
            }

            if (_job.IsCompleted)
            {
                // Everything is on disk but the read came back empty; re-open the final file and try again.
                if (_file is not null)
                {
                    await _file.DisposeAsync().ConfigureAwait(false);
                    _file = null;
                }

                if (DateTime.UtcNow - stalledSince > _stallTimeout)
                {
                    throw new IOException("The cached file could not be read after the download completed.");
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (await _job.WaitForProgressAsync(_progressWait, cancellationToken).ConfigureAwait(false))
            {
                stalledSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - stalledSince > _stallTimeout)
            {
                throw new IOException("The download from the parent server stalled.");
            }
        }
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        Position = target;
        return target;
    }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _file?.Dispose();
                _file = null;
                _onDispose?.Invoke();
            }
        }

        base.Dispose(disposing);
    }

    private async ValueTask<int> ReadFromFileAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_file is null)
        {
            var path = _job.IsCompleted && File.Exists(_job.Path) ? _job.Path : _job.PartPath;
            if (!File.Exists(path))
            {
                return 0;
            }

            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous);
        }

        if (_file.Position != _position)
        {
            _file.Seek(_position, SeekOrigin.Begin);
        }

        return await _file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
}
