using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class LoadAvgParserTests
{
    [Fact]
    public void ParseReadsLoadsAndProcessCounts()
    {
        var sample = LoadAvgParser.Parse("0.52 0.58 0.59 2/1234 56789\n");

        Assert.NotNull(sample);
        Assert.Equal(0.52, sample!.Load1, 6);
        Assert.Equal(0.58, sample.Load5, 6);
        Assert.Equal(0.59, sample.Load15, 6);
        Assert.Equal(2, sample.RunnableCount);
        Assert.Equal(1234, sample.TotalThreads);
    }

    [Fact]
    public void ParseToleratesMissingProcessCounts()
    {
        var sample = LoadAvgParser.Parse("1.00 2.00 3.00\n");

        Assert.NotNull(sample);
        Assert.Equal(1d, sample!.Load1, 6);
        Assert.Equal(0, sample.RunnableCount);
        Assert.Equal(0, sample.TotalThreads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc def ghi")]
    [InlineData("0.5 0.6")]
    [InlineData("-1 0 0")]
    public void ParseReturnsNullForUnusableInput(string? text)
    {
        Assert.Null(LoadAvgParser.Parse(text));
    }
}

public sealed class UptimeParserTests
{
    [Fact]
    public void ParseReadsSecondsAndTruncatesFraction()
    {
        var uptime = UptimeParser.Parse("12345.67 8901.23\n");

        Assert.NotNull(uptime);
        Assert.Equal(TimeSpan.FromSeconds(12345.67), uptime!.Value);
    }

    [Fact]
    public void ParseReadsZero()
    {
        Assert.Equal(TimeSpan.Zero, UptimeParser.Parse("0.00 0.00"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("-5.0 9.0")]
    public void ParseReturnsNullForUnusableInput(string? text)
    {
        Assert.Null(UptimeParser.Parse(text));
    }
}
