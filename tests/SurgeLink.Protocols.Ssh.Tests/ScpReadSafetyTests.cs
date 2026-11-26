using SurgeLink.Protocols.Ssh.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Security;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class ScpReadSafetyTests
{
    [Theory]
    [InlineData("/tmp/file\rname")]
    [InlineData("/tmp/file\nname")]
    public async Task ScpUploadRejectsUnsupportedFileNamesBeforeOpeningAConnection(string path)
    {
        var request = new ConnectionRequest { Host = "unused.test", Username = "user" };
        var options = new SshProtocolOptions(request);
        var context = new ProtocolHostContext(NullLoggerFactory.Instance,
            new JsonHostKeyStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "hosts.json")));
        var factory = new SshConnectionFactory(request, options, context, NullLogger.Instance);
        await using var files = new ScpFileSystem(factory, options, NullLogger.Instance);

        var error = Assert.Throws<ProtocolException>(() => { _ = files.OpenWriteAsync(path, null, null, default); });
        Assert.Equal(ProtocolFailureKind.Configuration, error.Kind);
    }

    [Fact]
    public void ReadCommandChecksTheTypeInTheSameCommand()
    {
        Assert.Equal("test -f '/tmp/data' && test ! -L '/tmp/data' && cat -- '/tmp/data'", ScpFileSystem.BuildReadCommand("/tmp/data"));
        Assert.Equal("test -f 'a'\\''b' && test ! -L 'a'\\''b' && cat -- 'a'\\''b'", ScpFileSystem.BuildReadCommand("a'b"));
    }

    [Fact]
    public async Task IdleReadTimesOutInsteadOfHoldingTheTransferLock()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => SshCommandRunner.CopyWithIdleTimeoutAsync(
            new StalledStream(), new MemoryStream(),
            TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationReleasesAStalledRead()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SshCommandRunner.CopyWithIdleTimeoutAsync(
            new StalledStream(), new MemoryStream(),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), cancellation.Token));
    }

    [Fact]
    public async Task CommandExitAfterStdoutEndIsBounded()
    {
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<TimeoutException>(() => SshCommandRunner.WaitForCompletionAsync(
            stalled.Task, Task.CompletedTask, TimeSpan.FromMilliseconds(100), CancellationToken.None));
    }

    [Fact]
    public async Task CommandExitAndStderrDrainCompleteTogether()
    {
        await SshCommandRunner.WaitForCompletionAsync(
            Task.CompletedTask, Task.CompletedTask, TimeSpan.FromSeconds(1), CancellationToken.None);
    }

    [Fact]
    public async Task CommandExitWaitObservesCancellation()
    {
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SshCommandRunner.WaitForCompletionAsync(
            stalled.Task, Task.CompletedTask, TimeSpan.FromSeconds(30), cancellation.Token));
    }

    [Fact]
    public async Task ReportedSizeIsAStrictUpperBound()
    {
        var source = new ChunkedReadStream(new byte[60], 25);
        var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => SshCommandRunner.CopyWithIdleTimeoutAsync(
            source, destination, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            CancellationToken.None, expectedBytes: 40));

        Assert.Contains("reported size of 40", exception.Message);
        Assert.Contains("grew", exception.Message);
        Assert.Equal(25, destination.Length);
    }

    [Fact]
    public async Task ZeroReportedSizeRejectsSpecialFileOutput()
    {
        var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => SshCommandRunner.CopyWithIdleTimeoutAsync(
            new MemoryStream(new byte[] { 1 }), destination,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            CancellationToken.None, expectedBytes: 0));

        Assert.Contains("grew", exception.Message);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task ShrinkingFileFailsAtEndOfStream()
    {
        var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => SshCommandRunner.CopyWithIdleTimeoutAsync(
            new MemoryStream(new byte[10]), destination,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            CancellationToken.None, expectedBytes: 20));

        Assert.Contains("shrank", exception.Message);
        Assert.Contains("reported size of 20", exception.Message);
        Assert.Equal(10, destination.Length);
    }

    [Fact]
    public async Task ExactReportedSizeSucceeds()
    {
        var destination = new MemoryStream();

        await SshCommandRunner.CopyWithIdleTimeoutAsync(
            new MemoryStream(new byte[10]), destination,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            CancellationToken.None, expectedBytes: 10);

        Assert.Equal(10, destination.Length);
    }

    [Fact]
    public async Task RemoteReadStreamPropagatesTheSizeFailureToItsReader()
    {
        var source = new ChunkedReadStream(new byte[60], 25);
        await using var stream = new RemoteReadStream(
            (sink, token) => SshCommandRunner.CopyWithIdleTimeoutAsync(
                source, sink, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), token, expectedBytes: 40),
            CancellationToken.None);

        var buffer = new byte[16];
        var exception = await Record.ExceptionAsync(async () =>
        {
            while (await stream.ReadAsync(buffer) > 0) { }
        });

        var protocol = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("grew", protocol.Message);
    }

    [Fact]
    public async Task StalledDestinationWriteTimesOut()
    {
        var source = new MemoryStream(new byte[16]);
        var destination = new StalledWriteStream();

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => SshCommandRunner.CopyWithIdleTimeoutAsync(
            source, destination, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
            CancellationToken.None));

        Assert.Contains("destination", exception.Message);
    }

    [Fact]
    public async Task DestinationWriteObservesCancellation()
    {
        var source = new MemoryStream(new byte[16]);
        var destination = new StalledWriteStream();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SshCommandRunner.CopyWithIdleTimeoutAsync(
            source, destination, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), cancellation.Token));
    }

    [Fact]
    public async Task WriteTimeoutRequestsCancellationOfTheRealWrite()
    {
        var destination = new CancelAwareStalledWriteStream();

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => SshCommandRunner.CopyWithIdleTimeoutAsync(
            new MemoryStream(new byte[16]), destination,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), CancellationToken.None));

        Assert.Contains("destination", exception.Message);
        await destination.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DownloadSinkAsyncWriteReturnsWhileTheChannelIsFull()
    {
        var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = new RemoteReadStream(async (sink, token) =>
        {
            var chunk = new byte[RemoteReadStream.ChunkSize];
            await sink.WriteAsync(chunk.AsMemory(), token);
            await sink.WriteAsync(chunk.AsMemory(), token);
            var pending = sink.WriteAsync(chunk.AsMemory(), token);
            blocked.TrySetResult(!pending.IsCompleted);
            await pending;
        }, CancellationToken.None);

        Assert.True(await blocked.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task CancellingTheStreamUnblocksABackpressuredAsyncWrite()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new RemoteReadStream(async (sink, token) =>
        {
            var chunk = new byte[RemoteReadStream.ChunkSize];
            await sink.WriteAsync(chunk.AsMemory(), token);
            await sink.WriteAsync(chunk.AsMemory(), token);
            var pending = sink.WriteAsync(chunk.AsMemory(), token);
            blocked.TrySetResult();
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        }, CancellationToken.None);

        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationStopsABackpressuredSinkWrite(bool byteArrayOverload)
    {
        using var caller = new CancellationTokenSource();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new RemoteReadStream(async (sink, token) =>
        {
            var chunk = new byte[RemoteReadStream.ChunkSize];
            await sink.WriteAsync(chunk.AsMemory(), token);
            await sink.WriteAsync(chunk.AsMemory(), token);
            Task pending = byteArrayOverload
                ? sink.WriteAsync(chunk, 0, chunk.Length, caller.Token)
                : sink.WriteAsync(chunk.AsMemory(), caller.Token).AsTask();
            blocked.TrySetResult();
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        }, CancellationToken.None);

        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(1));
            caller.Cancel();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task DownloadSinkCopiesTheCallerBufferBeforeEnqueueing()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = new RemoteReadStream(async (sink, token) =>
        {
            var buffer = new byte[] { 1, 2, 3, 4 };
            await sink.WriteAsync(buffer.AsMemory(), token);
            buffer[0] = 9;
            await sink.WriteAsync(buffer.AsMemory(), token);
            done.TrySetResult();
        }, CancellationToken.None);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var first = new byte[4];
        Assert.Equal(4, await stream.ReadAsync(first));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, first);

        var second = new byte[4];
        Assert.Equal(4, await stream.ReadAsync(second));
        Assert.Equal(new byte[] { 9, 2, 3, 4 }, second);
    }

    [Fact]
    public async Task DataWithinTheIdleBudgetIsCopied()
    {
        var content = new byte[100_000];
        Random.Shared.NextBytes(content);
        var destination = new MemoryStream();

        await SshCommandRunner.CopyWithIdleTimeoutAsync(new MemoryStream(content), destination,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(content, destination.ToArray());
    }

    private sealed class StalledStream : Stream
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _read.Task;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(_read.Task);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ChunkedReadStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }

    private sealed class StalledWriteStream : Stream
    {
        private readonly TaskCompletionSource _write = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(_write.Task);
    }

    private sealed class CancelAwareStalledWriteStream : Stream
    {
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    Cancelled.TrySetResult();
                    write.TrySetCanceled(cancellationToken);
                });
            }

            return new ValueTask(write.Task);
        }
    }
}
