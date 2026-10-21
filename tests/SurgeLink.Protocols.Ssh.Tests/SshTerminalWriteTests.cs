using System.IO;
using SurgeLink.Protocols.Ssh;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class SshTerminalWriteTests
{
    [Fact]
    public void InteractiveWriteFlushesTheShellStreamBuffer()
    {
        var stream = new FlushTrackingStream();

        SshTerminal.WriteToStream(stream, new byte[] { 0x65, 0x63, 0x68, 0x6F, 0x0D });

        Assert.Equal(new byte[] { 0x65, 0x63, 0x68, 0x6F, 0x0D }, stream.ToArray());
        Assert.Equal(1, stream.FlushCount);
    }

    private sealed class FlushTrackingStream : MemoryStream
    {
        public int FlushCount { get; private set; }

        public override void Flush()
        {
            FlushCount++;
            base.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return base.FlushAsync(cancellationToken);
        }
    }
}
