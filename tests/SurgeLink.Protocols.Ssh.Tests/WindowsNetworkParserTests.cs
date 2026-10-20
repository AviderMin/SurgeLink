using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class WindowsNetworkParserTests
{
    [Fact]
    public void ParseReadsAdapterStatistics()
    {
        const string section = """
            Ethernet|1000|2000|10|20
            Wi-Fi|3000|4000|30|40
            """;

        var networks = WindowsNetworkParser.Parse(section);

        Assert.Equal(2, networks.Count);
        Assert.Equal("Ethernet", networks[0].InterfaceName);
        Assert.Equal(1000, networks[0].BytesReceived);
        Assert.Equal(2000, networks[0].BytesSent);
        Assert.Equal(10, networks[0].PacketsReceived);
        Assert.Equal(20, networks[0].PacketsSent);
        Assert.Equal("Wi-Fi", networks[1].InterfaceName);
    }

    [Fact]
    public void ParseAcceptsAdapterNamesWithSpaces()
    {
        const string section = "Local Area Connection 2|100|200\n";

        var networks = WindowsNetworkParser.Parse(section);

        Assert.Single(networks);
        Assert.Equal("Local Area Connection 2", networks[0].InterfaceName);
    }

    [Fact]
    public void ParseAcceptsRowsWithoutPacketCounters()
    {
        const string section = "Loopback|100|200\n";

        var networks = WindowsNetworkParser.Parse(section);

        Assert.Single(networks);
        Assert.Equal(100, networks[0].BytesReceived);
        Assert.Equal(0, networks[0].PacketsReceived);
        Assert.Equal(0, networks[0].PacketsSent);
    }

    [Fact]
    public void ParseSkipsMalformedRows()
    {
        const string section = """
            no pipes here
            |100|200
            Bad|abc|200
            Bad2|100|-5
            Good|7|8|9|10
            """;

        var networks = WindowsNetworkParser.Parse(section);

        Assert.Single(networks);
        Assert.Equal("Good", networks[0].InterfaceName);
    }

    [Fact]
    public void ParseToleratesLargeCumulativeCounters()
    {
        const string section = "Ethernet|9223372036854775800|1|2|3\n";

        var networks = WindowsNetworkParser.Parse(section);

        Assert.Single(networks);
        Assert.Equal(9223372036854775800L, networks[0].BytesReceived);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void ParseReturnsEmptyForBlankSections(string? section)
    {
        Assert.Empty(WindowsNetworkParser.Parse(section));
    }
}
