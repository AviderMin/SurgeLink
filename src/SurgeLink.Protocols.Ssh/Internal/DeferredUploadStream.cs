namespace SurgeLink.Protocols.Ssh.Internal;

/// <summary>Spools SCP content locally. Cancellation or write failure aborts the deferred upload.</summary>
internal sealed class DeferredUploadStream(FileStream file, string temporaryPath,
    Func<CancellationToken, Task> upload, CancellationToken lifetime) : Stream
{
    private int _disposed;
    private bool _failed;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => _disposed == 0;
    public override long Length => file.Length;
    public override long Position { get => file.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush()
    {
        try { lifetime.ThrowIfCancellationRequested(); file.Flush(); }
        catch { _failed = true; throw; }
    }
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        try { lifetime.ThrowIfCancellationRequested(); await file.FlushAsync(cancellationToken).ConfigureAwait(false); }
        catch { _failed = true; throw; }
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        try { lifetime.ThrowIfCancellationRequested(); file.Write(buffer, offset, count); }
        catch { _failed = true; throw; }
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try { lifetime.ThrowIfCancellationRequested(); await file.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch { _failed = true; throw; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        try
        {
            await file.DisposeAsync().ConfigureAwait(false);
            if (!_failed && !lifetime.IsCancellationRequested) { await upload(lifetime).ConfigureAwait(false); }
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
