using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class WindowsCpuParserTests
{
    private const string PerfSection = """
        _Total|12.5
        0|10.0
        1|15.0
        """;

    [Fact]
    public void ParseReadsTotalAndPerCorePercentages()
    {
        var sample = WindowsCpuParser.Parse(PerfSection, null);

        Assert.Equal(12.5, sample.TotalPercent, 6);
        Assert.Equal(2, sample.PerCorePercent.Count);
        Assert.Equal(10d, sample.PerCorePercent[0], 6);
        Assert.Equal(15d, sample.PerCorePercent[1], 6);
        Assert.Equal(2, sample.LogicalProcessorCount);
    }

    [Fact]
    public void ParseAveragesCoresWhenTheTotalRowIsMissing()
    {
        var sample = WindowsCpuParser.Parse("0|40\n1|60\n", null);

        Assert.Equal(50d, sample.TotalPercent, 6);
        Assert.Equal(2, sample.LogicalProcessorCount);
    }

    [Fact]
    public void ParseOrdersCoresByIndex()
    {
        var sample = WindowsCpuParser.Parse("1|20\n0|10\n", null);

        Assert.Equal(new[] { 10d, 20d }, sample.PerCorePercent);
    }

    [Fact]
    public void ParseKeepsGapsBetweenCoreIndices()
    {
        var sample = WindowsCpuParser.Parse("0|10\n2|30\n", null);

        Assert.Equal(3, sample.PerCorePercent.Count);
        Assert.Equal(10d, sample.PerCorePercent[0], 6);
        Assert.Equal(0d, sample.PerCorePercent[1], 6);
        Assert.Equal(30d, sample.PerCorePercent[2], 6);
    }

    [Fact]
    public void ParseFallsBackToWin32ProcessorLoad()
    {
        var sample = WindowsCpuParser.Parse(null, "37|8|4");

        Assert.Equal(37d, sample.TotalPercent, 6);
        Assert.Empty(sample.PerCorePercent);
        Assert.Equal(8, sample.LogicalProcessorCount);
    }

    [Fact]
    public void ParsePrefersPerfDataOverProcessorLoad()
    {
        var sample = WindowsCpuParser.Parse(PerfSection, "90|2|1");

        Assert.Equal(12.5, sample.TotalPercent, 6);
        Assert.Equal(2, sample.LogicalProcessorCount);
    }

    [Fact]
    public void ParseAveragesAndSumsMultipleProcessorRows()
    {
        var sample = WindowsCpuParser.Parse(null, "20|4|2\n40|4|2\n");

        Assert.Equal(30d, sample.TotalPercent, 6);
        Assert.Equal(8, sample.LogicalProcessorCount);
    }

    [Fact]
    public void ParseSkipsMalformedRows()
    {
        var sample = WindowsCpuParser.Parse("garbage\n|5\n0|abc\n1|70\n", "not a row");

        Assert.Equal(2, sample.PerCorePercent.Count);
        Assert.Equal(0d, sample.PerCorePercent[0], 6);
        Assert.Equal(70d, sample.PerCorePercent[1], 6);
        Assert.Equal(35d, sample.TotalPercent, 6);
    }

    [Fact]
    public void ParseClampsOutOfRangePercentages()
    {
        var sample = WindowsCpuParser.Parse("0|150\n1|-20\n", null);

        Assert.Equal(100d, sample.PerCorePercent[0], 6);
        Assert.Equal(0d, sample.PerCorePercent[1], 6);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void ParseReturnsEmptyForBlankSections(string? cpuSection, string? processorSection)
    {
        var sample = WindowsCpuParser.Parse(cpuSection, processorSection);

        Assert.Equal(0d, sample.TotalPercent);
        Assert.Empty(sample.PerCorePercent);
        Assert.Equal(0, sample.LogicalProcessorCount);
    }
}