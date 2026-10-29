using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class MonitorOutputTests
{
    [Fact]
    public void SplitReturnsEverySection()
    {
        const string output = "@@@SURGELINK:stat\ncpu 1 2 3 4\n@@@SURGELINK:meminfo\nMemTotal: 100 kB\n@@@SURGELINK:df\n/dev/sda1 1 2 3 25% /\n";

        var sections = MonitorOutput.Split(output);

        Assert.Equal(3, sections.Count);
        Assert.Equal("cpu 1 2 3 4\n", sections["stat"]);
        Assert.Equal("MemTotal: 100 kB\n", sections["meminfo"]);
        Assert.Equal("/dev/sda1 1 2 3 25% /\n", sections["df"]);
    }

    [Fact]
    public void SplitHandlesCrLf()
    {
        const string output = "@@@SURGELINK:stat\r\ncpu 1 2 3 4\r\n";

        var sections = MonitorOutput.Split(output);

        Assert.Equal("cpu 1 2 3 4\n", sections["stat"]);
    }

    [Fact]
    public void SplitKeepsEmptySections()
    {
        const string output = "@@@SURGELINK:stat\n@@@SURGELINK:meminfo\nMemTotal: 1 kB\n";

        var sections = MonitorOutput.Split(output);

        Assert.True(sections.ContainsKey("stat"));
        Assert.Equal(string.Empty, sections["stat"]);
    }

    [Fact]
    public void SplitIgnoresTextBeforeTheFirstMarker()
    {
        const string output = "Welcome to Ubuntu\n@@@SURGELINK:stat\ncpu 1 2 3 4\n";

        var sections = MonitorOutput.Split(output);

        Assert.Single(sections);
        Assert.Equal("cpu 1 2 3 4\n", sections["stat"]);
    }

    [Fact]
    public void SplitToleratesMarkerWhitespace()
    {
        const string output = "  @@@SURGELINK:stat  \ncpu 1 2 3 4";

        var sections = MonitorOutput.Split(output);

        Assert.Equal("cpu 1 2 3 4\n", sections["stat"]);
    }

    [Fact]
    public void SplitReturnsLatestDuplicateSection()
    {
        const string output = "@@@SURGELINK:stat\nfirst\n@@@SURGELINK:stat\nsecond\n";

        var sections = MonitorOutput.Split(output);

        Assert.Equal("second\n", sections["stat"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SplitReturnsEmptyForUnusableInput(string? output)
    {
        Assert.Empty(MonitorOutput.Split(output));
    }
}

public sealed class MonitorScriptTests
{
    [Fact]
    public void CommandCollectsEveryCounter()
    {
        var command = MonitorScript.Command;

        Assert.Contains("cat /proc/stat", command, StringComparison.Ordinal);
        Assert.Contains("cat /proc/meminfo", command, StringComparison.Ordinal);
        Assert.Contains("cat /proc/loadavg", command, StringComparison.Ordinal);
        Assert.Contains("cat /proc/uptime", command, StringComparison.Ordinal);
        Assert.Contains("cat /proc/net/dev", command, StringComparison.Ordinal);
        Assert.Contains("cat /proc/diskstats", command, StringComparison.Ordinal);
        Assert.Contains("df -kP", command, StringComparison.Ordinal);
        Assert.Contains("/proc", command, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandEmitsEveryMarker()
    {
        var command = MonitorScript.Command;

        foreach (var section in new[]
                 {
                     MonitorScript.StatSection, MonitorScript.MemInfoSection, MonitorScript.LoadAvgSection,
                     MonitorScript.UptimeSection, MonitorScript.NetDevSection, MonitorScript.DiskStatsSection,
                     MonitorScript.DiskUsageSection, MonitorScript.ProcessCountSection,
                 })
        {
            Assert.Contains(MonitorOutput.MarkerPrefix + section, command, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuildCommandMatchesTheCachedCommand()
    {
        Assert.Equal(MonitorScript.Command, MonitorScript.BuildCommand());
    }

    [Fact]
    public void CommandStartsWithTheFirstMarker()
    {
        var firstLine = MonitorScript.Command.Split('\n')[0];

        Assert.Contains(MonitorOutput.MarkerPrefix + MonitorScript.StatSection, firstLine, StringComparison.Ordinal);
    }
}