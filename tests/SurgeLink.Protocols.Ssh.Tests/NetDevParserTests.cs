using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class NetDevParserTests
{
    private const string Sample = """
        Inter-|   Receive                                                |  Transmit
         face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
            lo: 1234567    8910    0    0    0     0          0         0  1234567    8910    0    0    0     0       0          0
          eth0: 987654321 1234567    3    1    0     0          0         0 123456789  987654    2    0    0     0       0          0
        """;

    [Fact]
    public void ParseReadsOneSamplePerInterface()
    {
        var samples = NetDevParser.Parse(Sample);

        Assert.Equal(2, samples.Count);
        var loopback = samples[0];
        Assert.Equal("lo", loopback.InterfaceName);
        Assert.Equal(1234567, loopback.BytesReceived);
        Assert.Equal(8910, loopback.PacketsReceived);
        Assert.Equal(1234567, loopback.BytesSent);
        Assert.Equal(8910, loopback.PacketsSent);

        var ethernet = samples[1];
        Assert.Equal("eth0", ethernet.InterfaceName);
        Assert.Equal(987654321, ethernet.BytesReceived);
        Assert.Equal(1234567, ethernet.PacketsReceived);
        Assert.Equal(123456789, ethernet.BytesSent);
        Assert.Equal(987654, ethernet.PacketsSent);
    }

    [Fact]
    public void ParseAcceptsDefinitionsWithoutSpacesAfterTheColon()
    {
        const string text = "eth0:1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16\n";

        var samples = NetDevParser.Parse(text);

        Assert.Single(samples);
        Assert.Equal(1, samples[0].BytesReceived);
        Assert.Equal(9, samples[0].BytesSent);
    }

    [Fact]
    public void ParseToleratesLargeSixtyFourBitCounters()
    {
        const string text = "eth0: 9223372036854775800 1 0 0 0 0 0 0 9223372036854775800 1 0 0 0 0 0 0\n";

        var samples = NetDevParser.Parse(text);

        Assert.Single(samples);
        Assert.Equal(9223372036854775800L, samples[0].BytesReceived);
    }

    [Fact]
    public void ParseSkipsRowsWithTooFewColumns()
    {
        const string text = "eth0: 1 2 3 4 5\neth1: 1 2 3 4 5 6 7 8 9 10\n";

        var samples = NetDevParser.Parse(text);

        Assert.Single(samples);
        Assert.Equal("eth1", samples[0].InterfaceName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no colons here")]
    [InlineData("    lo: 1 2")]
    public void ParseReturnsEmptyForUnusableInput(string? text)
    {
        Assert.Empty(NetDevParser.Parse(text));
    }
}
