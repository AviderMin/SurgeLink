using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class WindowsMemoryParserTests
{
    private const string Sample = """
        total|16318456
        free|1732544
        virtualTotal|20971520
        virtualFree|10485760
        """;

    private const long Kilobyte = 1024;

    [Fact]
    public void ParseConvertsKilobytesToBytes()
    {
        var memory = WindowsMemoryParser.Parse(Sample);

        Assert.Equal(16318456L * Kilobyte, memory.TotalBytes);
        Assert.Equal(1732544L * Kilobyte, memory.AvailableBytes);
        Assert.Equal(20971520L * Kilobyte, memory.SwapTotalBytes);
        Assert.Equal(10485760L * Kilobyte, memory.SwapUsedBytes);
        Assert.Equal(0, memory.CachedBytes);
    }

    [Fact]
    public void ParseTreatsMissingVirtualFreeAsFullyUsedSwap()
    {
        const string text = "total|1000\nfree|250\nvirtualTotal|500\n";

        var memory = WindowsMemoryParser.Parse(text);

        Assert.Equal(500L * Kilobyte, memory.SwapTotalBytes);
        Assert.Equal(500L * Kilobyte, memory.SwapUsedBytes);
    }

    [Fact]
    public void ParseClampsFreeMemoryToTotal()
    {
        const string text = "total|1000\nfree|5000\n";

        var memory = WindowsMemoryParser.Parse(text);

        Assert.Equal(1000L * Kilobyte, memory.AvailableBytes);
        Assert.Equal(0, memory.SwapTotalBytes);
    }

    [Fact]
    public void ParseSkipsMalformedLines()
    {
        const string text = "total|1000\nbogus line\nfree|200\nvirtualTotal|-5\nvirtualFree|abc\n";

        var memory = WindowsMemoryParser.Parse(text);

        Assert.Equal(1000L * Kilobyte, memory.TotalBytes);
        Assert.Equal(200L * Kilobyte, memory.AvailableBytes);
        Assert.Equal(0, memory.SwapTotalBytes);
        Assert.Equal(0, memory.SwapUsedBytes);
    }

    [Fact]
    public void ParseClampsHugeValuesInsteadOfOverflowing()
    {
        const string text = "total|9223372036854775807\nfree|1\n";

        var memory = WindowsMemoryParser.Parse(text);

        Assert.Equal(long.MaxValue, memory.TotalBytes);
        Assert.Equal(1024L, memory.AvailableBytes);
    }

    [Fact]
    public void ParseUsesCaseInsensitiveFieldNames()
    {
        const string text = "TOTAL|1000\nFREE|750\n";

        var memory = WindowsMemoryParser.Parse(text);

        Assert.Equal(1000L * Kilobyte, memory.TotalBytes);
        Assert.Equal(750L * Kilobyte, memory.AvailableBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void ParseReturnsZerosForBlankSections(string? text)
    {
        var memory = WindowsMemoryParser.Parse(text);

        Assert.Equal(0, memory.TotalBytes);
        Assert.Equal(0, memory.AvailableBytes);
        Assert.Equal(0d, memory.UsedFraction);
    }
}