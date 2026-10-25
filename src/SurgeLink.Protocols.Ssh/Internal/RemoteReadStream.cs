using System.Threading.Channels;

namespace SurgeLink.Protocols.Ssh.Internal;

/// <summary>Adapts the SSH/SCP fallback's producer to a bounded pull stream. Closing
/// the consumer cancels the transfer, including a producer blocked on backpressure.</summary>
internal sealed class RemoteReadStream : Stream
{
    internal const int ChunkSize = 32 * 1024;
    private readonly Channel<byte[]> _chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly CancellationTokenSource _lifetime;
    private readonly Task _producer;
    private byte[]? _current;
    private int _offset;
    private int _disposed;

    public RemoteReadStream(Func<Stream, CancellationToken, Task> download, CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _producer = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                using var sink = new DownloadSink(_chunks.Writer, _lifetime.Token);
                await download(sink, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { failure = ex; }
            finally { _chunks.Writer.TryComplete(failure); }
        }, CancellationToken.None);
    }

    public override bool CanRead => Volatile.Read(ref _disposed) == 0;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (buffer.IsEmpty) return 0;
        try
        {
            if (_current is null)
            {
                if (!await _chunks.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false)) return 0;
                if (!_chunks.Reader.TryRead(out _current)) throw new IOException("The remote download stream has multiple readers.");
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset == _current.Length) _current = null;
            return count;
        }
        catch (OperationCanceledException)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _producer.ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Dispose();
            _current = null;
            while (_chunks.Reader.TryRead(out _)) { }
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    private sealed class DownloadSink(ChannelWriter<byte[]> writer, CancellationToken lifetime) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lifetime.ThrowIfCancellationRequested();
            while (!buffer.IsEmpty)
            {
                lifetime.ThrowIfCancellationRequested();
                var count = Math.Min(buffer.Length, ChunkSize);
                writer.WriteAsync(buffer[..count].ToArray(), lifetime).AsTask().GetAwaiter().GetResult();
                buffer = buffer[count..];
            }
        }
    }
}
