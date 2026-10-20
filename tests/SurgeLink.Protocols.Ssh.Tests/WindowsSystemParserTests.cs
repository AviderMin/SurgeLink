using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class WindowsSystemParserTests
{
    private static readonly DateTimeOffset Now = new(2024, 1, 2, 4, 4, 5, TimeSpan.Zero);

    [Fact]
    public void ParseReadsCountersAndDerivesUptime()
    {
        const string section = """
            processes|123
            threads|4567
            queue|2
            boot|2024-01-02T03:04:05.0000000Z
            """;

        var sample = WindowsSystemParser.Parse(section, Now);

        Assert.Equal(123, sample.ProcessCount);
        Assert.Equal(4567, sample.ThreadCount);
        Assert.Equal(TimeSpan.FromHours(1), sample.Uptime);
    }

    [Fact]
    public void ParseTreatsBootTimesWithoutAnOffsetAsUtc()
    {
        const string section = "boot|2024-01-02T03:04:05\n";

        var sample = WindowsSystemParser.Parse(section, Now);

        Assert.Equal(TimeSpan.FromHours(1), sample.Uptime);
    }

    [Theory]
    [InlineData("processes|123\nthreads|4567\n")]
    [InlineData("boot|not-a-date\n")]
    [InlineData("boot|2025-01-02T03:04:05Z\n")]
    public void ParseLeavesUptimeUnknownWhenTheBootTimeIsUnusable(string section)
    {
        var sample = WindowsSystemParser.Parse(section, Now);

        Assert.Null(sample.Uptime);
    }

    [Fact]
    public void ParseIgnoresMalformedCounters()
    {
        const string section = "processes|abc\nthreads|-5\nqueue|9\n";

        var sample = WindowsSystemParser.Parse(section, Now);

        Assert.Equal(0, sample.ProcessCount);
        Assert.Equal(0, sample.ThreadCount);
        Assert.Null(sample.Uptime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void ParseReturnsEmptyForBlankSections(string? section)
    {
        var sample = WindowsSystemParser.Parse(section, Now);

        Assert.Equal(0, sample.ProcessCount);
        Assert.Equal(0, sample.ThreadCount);
        Assert.Null(sample.Uptime);
    }
}
