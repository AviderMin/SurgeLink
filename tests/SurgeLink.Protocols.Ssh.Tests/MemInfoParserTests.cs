using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class MemInfoParserTests
{
    private const string WithMemAvailable = """
        MemTotal:       16318456 kB
        MemFree:         1732544 kB
        MemAvailable:    9876543 kB
        Buffers:          123456 kB
        Cached:          4567890 kB
        SwapCached:            0 kB
        SwapTotal:       2097148 kB
        SwapFree:        2097148 kB
        HugePages_Total:       0
        """;

    private const string WithoutMemAvailable = """
        MemTotal:        8000000 kB
        MemFree:         1000000 kB
        Buffers:          200000 kB
        Cached:           800000 kB
        SwapTotal:       1000000 kB
        SwapFree:         750000 kB
        """;

    [Fact]
    public void ParseUsesMemAvailableWhenPresent()
    {
        var memory = MemInfoParser.Parse(WithMemAvailable);

        Assert.Equal(16318456L * 1024, memory.TotalBytes);
        Assert.Equal(9876543L * 1024, memory.AvailableBytes);
        Assert.Equal(4567890L * 1024, memory.CachedBytes);
        Assert.Equal(2097148L * 1024, memory.SwapTotalBytes);
        Assert.Equal(0, memory.SwapUsedBytes);
    }

    [Fact]
    public void ParseFallsBackToFreeBuffersCachedWithoutMemAvailable()
    {
        var memory = MemInfoParser.Parse(WithoutMemAvailable);

        Assert.Equal(8000000L * 1024, memory.TotalBytes);
        Assert.Equal(2000000L * 1024, memory.AvailableBytes);
        Assert.Equal(6000000L * 1024, memory.UsedBytes);
        Assert.Equal(250000L * 1024, memory.SwapUsedBytes);
    }

    [Fact]
    public void ParseFallsBackWhenMemAvailableIsZero()
    {
        const string text = "MemTotal: 1000 kB\nMemFree: 100 kB\nBuffers: 50 kB\nCached: 350 kB\nMemAvailable: 0 kB\n";

        var memory = MemInfoParser.Parse(text);

        Assert.Equal(500L * 1024, memory.AvailableBytes);
    }

    [Fact]
    public void ParseIgnoresMalformedLines()
    {
        const string text = """
            MemTotal:       1000 kB
            this line has no colon
            BadValue:       not-a-number kB
            MemFree:        100 kB
            Cached:         200 kB
            Buffers:         50 kB
            """;

        var memory = MemInfoParser.Parse(text);

        Assert.Equal(1000L * 1024, memory.TotalBytes);
        Assert.Equal(350L * 1024, memory.AvailableBytes);
    }

    [Fact]
    public void ParseClampsAvailableToTotal()
    {
        const string text = "MemTotal: 1000 kB\nMemAvailable: 5000 kB\n";

        var memory = MemInfoParser.Parse(text);

        Assert.Equal(1000L * 1024, memory.AvailableBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void ParseReturnsZerosForEmptyInput(string? text)
    {
        var memory = MemInfoParser.Parse(text);

        Assert.Equal(0, memory.TotalBytes);
        Assert.Equal(0, memory.AvailableBytes);
        Assert.Equal(0d, memory.UsedFraction);
    }
}
