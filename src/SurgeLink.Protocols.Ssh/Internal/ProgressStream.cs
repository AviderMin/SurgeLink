using SurgeLink.Core.Files;

namespace SurgeLink.Protocols.Ssh.Internal;

/// <summary>
/// Write only stream wrapper that reports upload progress and completes the remote
/// write when it is disposed.
/// </summary>
internal sealed class ProgressStream : Stream
{
    private const long ReportThresholdBytes = 64 * 1024;

    private readonly Stream _inner;
    private readonly long? _totalBytes;
    private readonly string _path;
    private readonly IProgress<TransferProgress>? _progress;
    private readonly bool _leaveOpen;
    private long _written;
    private long _lastReported;
    private bool _disposed;

    /// <summary>Wraps <paramref name="inner"/> and reports progress to <paramref name="progress"/>.</summary>
    public ProgressStream(Stream inner, long? totalBytes, string path, IProgress<TransferProgress>? progress, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _totalBytes = totalBytes is > 0 ? totalBytes : null;
        _path = path;
        _progress = progress;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed && _inner.CanWrite;

    /// <inheritdoc />
    public override long Length => _inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => _inner.SetLength(value);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Report(count);
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        Report(buffer.Length);
    }

    /// <inheritdoc />
    public override void WriteByte(byte value)
    {
        _inner.WriteByte(value);
        Report(1);
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsyncCore(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        WriteAsyncCore(buffer, cancellationToken);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            base.Dispose(disposing);
            return;
        }

        _disposed = true;
        try
        {
            ReportFinal();
        }
        finally
        {
            if (!_leaveOpen)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private async ValueTask WriteAsyncCore(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Report(buffer.Length);
    }

    private void Report(int bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        _written += bytes;
        if (_progress is null)
        {
            return;
        }

        var thresholdReached = _written - _lastReported >= ReportThresholdBytes;
        var uploadComplete = _totalBytes is not null && _written >= _totalBytes.Value;
        if (!thresholdReached && !uploadComplete)
        {
            return;
        }

        _lastReported = _written;
        _progress.Report(new TransferProgress(_written, _totalBytes, _path));
    }

    private void ReportFinal()
    {
        if (_progress is null || _written == _lastReported)
        {
            return;
        }

        _lastReported = _written;
        _progress.Report(new TransferProgress(_written, _totalBytes, _path));
    }
}
