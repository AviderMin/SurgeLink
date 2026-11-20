using SurgeLink.Protocols.Ssh.Internal;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class ScpReadSafetyTests
{
    [Fact]
    public void ReadCommandChecksTheTypeInTheSameCommand()
    {
        Assert.Equal("test -f '/tmp/data' && cat -- '/tmp/data'", ScpFileSystem.BuildReadCommand("/tmp/data"));
        Assert.Equal("test -f 'a'\\''b' && cat -- 'a'\\''b'", ScpFileSystem.BuildReadCommand("a'b"));
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
}
