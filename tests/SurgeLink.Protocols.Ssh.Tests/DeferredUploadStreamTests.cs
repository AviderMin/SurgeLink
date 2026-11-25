using SurgeLink.Protocols.Ssh.Internal;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class DeferredUploadStreamTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlySuccessfulUncancelledStreamsCommit(bool cancel)
    {
        var path = Path.Combine(Path.GetTempPath(), "surge-scp-test-" + Guid.NewGuid().ToString("N"));
        using var lifetime = new CancellationTokenSource();
        var uploads = 0;
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        var stream = new DeferredUploadStream(file, path, token =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(1, new FileInfo(path).Length);
            uploads++;
            return Task.CompletedTask;
        }, lifetime.Token);
        await stream.WriteAsync(new byte[] { 42 });
        if (cancel)
        {
            lifetime.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.CompleteAsync(CancellationToken.None));
        }
        else
        {
            await stream.CompleteAsync(CancellationToken.None);
        }
        await stream.DisposeAsync();
        await stream.DisposeAsync();
        Assert.Equal(cancel ? 0 : 1, uploads);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DisposingSpoolAfterProducerFailsDoesNotUpload()
    {
        var path = Path.Combine(Path.GetTempPath(), "surge-scp-test-" + Guid.NewGuid().ToString("N"));
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        var uploads = 0;
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using var stream = new DeferredUploadStream(file, path, _ => { uploads++; return Task.CompletedTask; }, CancellationToken.None);
            await stream.WriteAsync(new byte[] { 42 });
            await stream.FlushAsync();
            throw new IOException("Source read failed.");
        });
        Assert.Equal(0, uploads);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task FailedSpoolWriteDoesNotUpload()
    {
        var path = Path.Combine(Path.GetTempPath(), "surge-scp-test-" + Guid.NewGuid().ToString("N"));
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        var uploads = 0;
        var stream = new DeferredUploadStream(file, path, _ => { uploads++; return Task.CompletedTask; }, CancellationToken.None);
        await file.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.WriteAsync(new byte[] { 42 }).AsTask());
        await stream.DisposeAsync();
        Assert.Equal(0, uploads);
        Assert.False(File.Exists(path));
    }
}
