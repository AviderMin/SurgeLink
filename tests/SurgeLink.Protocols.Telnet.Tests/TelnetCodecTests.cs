using System.Text;
using Xunit;

namespace SurgeLink.Protocols.Telnet.Tests;

public sealed class TelnetCodecTests
{
    [Fact]
    public void CommandsAndEscapedIacSurviveEveryPacketSplit()
    {
        byte[] wire = [(byte)'A', 255, 251, 1, 255, 255, 255, 241, (byte)'B', 255, 250, 42, 9, 255, 240, (byte)'C'];
        for (int split = 0; split <= wire.Length; split++)
        {
            var codec = Create();
            var first = codec.Receive(wire.AsSpan(0, split));
            var second = codec.Receive(wire.AsSpan(split));
            Assert.Equal(new byte[] { (byte)'A', 255, (byte)'B', (byte)'C' }, first.Data.Concat(second.Data));
            Assert.Equal(new byte[] { 255, 253, 1 }, first.Reply.Concat(second.Reply));
        }
    }

    [Fact]
    public void DuplicateNegotiationsDoNotLoopAndDisabledOptionsCanBeEnabledAgain()
    {
        var codec = Create();
        Assert.Equal(new byte[] { 255, 253, 1, 255, 251, 3 }, codec.Receive([255, 251, 1, 255, 253, 3]).Reply);
        Assert.Empty(codec.Receive([255, 251, 1, 255, 253, 3]).Reply);
        Assert.Equal(new byte[] { 255, 254, 1, 255, 252, 3 }, codec.Receive([255, 252, 1, 255, 254, 3]).Reply);
        Assert.Empty(codec.Receive([255, 252, 1, 255, 254, 3]).Reply);
        Assert.Equal(new byte[] { 255, 253, 1 }, codec.Receive([255, 251, 1]).Reply);
    }

    [Fact]
    public void InitialRequestsAreAcknowledgedWithoutBeingRepeated()
    {
        var codec = Create();
        Assert.Equal(21, codec.BeginNegotiation().Length);
        Assert.Empty(codec.Receive([255, 253, 0, 255, 251, 0, 255, 251, 1]).Reply);
        Assert.Empty(codec.Receive([255, 254, 24, 255, 252, 3]).Reply);
    }

    [Fact]
    public void UnsupportedOptionsAndClientEchoAreRejected()
    {
        var parsed = Create().Receive([255, 253, 1, 255, 251, 42, 255, 253, 42]);
        Assert.Equal(new byte[] { 255, 252, 1, 255, 254, 42, 255, 252, 42 }, parsed.Reply);
        Assert.Empty(parsed.Data);
    }

    [Fact]
    public void WindowSizeIsSentOnlyAfterAcceptanceAndEscapes255()
    {
        var codec = Create();
        Assert.Empty(codec.Resize(255, 511));
        byte[] expected = [255, 250, 31, 0, 255, 255, 1, 255, 255, 255, 240];
        Assert.Equal(new byte[] { 255, 251, 31 }.Concat(expected), codec.Receive([255, 253, 31]).Reply);
        Assert.Equal(expected, codec.Resize(255, 511));
        Assert.Empty(codec.Receive([255, 253, 31]).Reply);
        codec.Receive([255, 254, 31]);
        Assert.Empty(codec.Resize(80, 24));
    }

    [Fact]
    public void TerminalTypeRequiresNegotiationAndHandlesByteByByteRequests()
    {
        var codec = Create();
        Assert.Empty(codec.Receive([255, 250, 24, 1, 255, 240]).Reply);
        codec.Receive([255, 253, 24]);
        var reply = new List<byte>();
        foreach (byte b in new byte[] { 255, 250, 24, 1, 255, 240 }) reply.AddRange(codec.Receive([b]).Reply);
        Assert.Equal(new byte[] { 255, 250, 24, 0 }.Concat(Encoding.ASCII.GetBytes("xterm-256color")).Concat(new byte[] { 255, 240 }), reply);
    }

    [Fact]
    public void OversizedSubnegotiationIsDiscardedAndParserRecovers()
    {
        var codec = Create();
        codec.Receive([255, 253, 24]);
        var parsed = codec.Receive(new byte[] { 255, 250, 24, 1 }
            .Concat(Enumerable.Repeat((byte)65, 10000)).Concat(new byte[] { 255, 240, 90 }).ToArray());
        Assert.Empty(parsed.Reply);
        Assert.Equal(new byte[] { 90 }, parsed.Data);
    }

    [Fact]
    public void NvtNormalizesNewlinesAndStripsCrPaddingAcrossPackets()
    {
        var codec = Create();
        Assert.Equal(new byte[] { 13, 0, 13, 10, 13, 10, 255, 255 }, codec.Encode([13, 13, 10, 10, 255]));
        Assert.Equal(new byte[] { 13 }, codec.Receive([13]).Data);
        Assert.Equal(new byte[] { 13, 10, 0 }, codec.Receive([0, 13, 10, 0]).Data);
    }

    [Fact]
    public void BinaryNegotiationIsIndependentInEachDirection()
    {
        var codec = Create();
        codec.Receive([255, 253, 0]);
        Assert.Equal(new byte[] { 13, 0, 255, 255 }, codec.Encode([13, 0, 255]));
        Assert.Equal(new byte[] { 13 }, codec.Receive([13, 0]).Data);
        codec.Receive([255, 251, 0]);
        Assert.Equal(new byte[] { 13, 0 }, codec.Receive([13, 0]).Data);
    }

    private static TelnetCodec Create() => new(80, 24, "xterm-256color");
}
