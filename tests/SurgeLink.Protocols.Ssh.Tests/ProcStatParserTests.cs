using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class ProcStatParserTests
{
    private const string ModernSample = """
        cpu  4325435 1234 2345456 38342456 23456 0 1234 0 0 0
        cpu0 1081358 308 586364 9585614 5864 0 308 0 0 0
        cpu1 1081359 308 586364 9585614 5864 0 308 0 0 0
        cpu2 1081359 309 586364 9585614 5864 0 309 0 0 0
        cpu3 1081359 309 586364 9585614 5864 0 309 0 0 0
        intr 1234567 12 13 14
        ctxt 98765432
        btime 1700000000
        procs_running 2
        procs_blocked 0
        """;

    private const string Kernel26Sample = """
        cpu  12345 678 9012 34567
        cpu0 3000 100 2000 8000
        cpu1 9345 578 7012 26567
        """;

    [Fact]
    public void ParseReadsAggregateAndPerCoreCounters()
    {
        var sample = ProcStatParser.Parse(ModernSample);

        Assert.NotNull(sample);
        var total = sample!.Total;
        Assert.Equal(4325435, total.User);
        Assert.Equal(1234, total.Nice);
        Assert.Equal(2345456, total.System);
        Assert.Equal(38342456, total.Idle);
        Assert.Equal(23456, total.Iowait);
        Assert.Equal(0, total.Irq);
        Assert.Equal(1234, total.SoftIrq);
        Assert.Equal(0, total.Steal);
        Assert.Equal(4, sample.PerCore.Count);
        Assert.Equal(1081358, sample.PerCore[0].User);
        Assert.Equal(309, sample.PerCore[3].Nice);
    }

    [Fact]
    public void ParseSupportsKernel26ShortLines()
    {
        var sample = ProcStatParser.Parse(Kernel26Sample);

        Assert.NotNull(sample);
        Assert.Equal(12345, sample!.Total.User);
        Assert.Equal(678, sample.Total.Nice);
        Assert.Equal(9012, sample.Total.System);
        Assert.Equal(34567, sample.Total.Idle);
        Assert.Equal(0, sample.Total.Iowait);
        Assert.Equal(2, sample.PerCore.Count);
        Assert.Equal(26567, sample.PerCore[1].Idle);
    }

    [Fact]
    public void ParseAcceptsCrLfLineEndings()
    {
        var sample = ProcStatParser.Parse(ModernSample.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.NotNull(sample);
        Assert.Equal(4, sample!.PerCore.Count);
    }

    [Fact]
    public void ParseToleratesTruncatedAggregateLine()
    {
        var sample = ProcStatParser.Parse("cpu 1 2 3");

        Assert.NotNull(sample);
        Assert.Equal(1, sample!.Total.User);
        Assert.Equal(2, sample.Total.Nice);
        Assert.Equal(3, sample.Total.System);
        Assert.Equal(0, sample.Total.Idle);
    }

    [Fact]
    public void ParseSkipsMalformedCoreLinesButKeepsIndices()
    {
        const string text = "cpu 10 20 30 40\ncpu0 not-a-number 1 2 3\ncpu1 5 6 7 8\n";

        var sample = ProcStatParser.Parse(text);

        Assert.NotNull(sample);
        Assert.Equal(2, sample!.PerCore.Count);
        Assert.Equal(default(ProcCpuTimes), sample.PerCore[0]);
        Assert.Equal(5, sample.PerCore[1].User);
    }

    [Fact]
    public void ParseExpandsCoreListForSparseIndices()
    {
        var sample = ProcStatParser.Parse("cpu 1 2 3 4\ncpu2 10 20 30 40\n");

        Assert.NotNull(sample);
        Assert.Equal(3, sample!.PerCore.Count);
        Assert.Equal(default(ProcCpuTimes), sample.PerCore[0]);
        Assert.Equal(default(ProcCpuTimes), sample.PerCore[1]);
        Assert.Equal(10, sample.PerCore[2].User);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("intr 1\nctxt 2\n")]
    [InlineData("cpu\ncpu0 abc def\n")]
    public void ParseReturnsNullWhenNoCpuCountersArePresent(string? text)
    {
        Assert.Null(ProcStatParser.Parse(text));
    }

    [Fact]
    public void TotalAndIdleTotalsSumTheRightFields()
    {
        var counters = new ProcCpuTimes(10, 20, 30, 40, 5, 6, 7, 8);

        Assert.Equal(126, counters.Total);
        Assert.Equal(45, counters.IdleTotal);
    }
}
