using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class CpuUsageCalculatorTests
{
    [Fact]
    public void FirstSampleReportsZero()
    {
        var current = new ProcStatSample
        {
            Total = new ProcCpuTimes(100, 0, 100, 800),
            PerCore = new[] { new ProcCpuTimes(100, 0, 100, 800), new ProcCpuTimes(50, 0, 50, 900) },
        };

        var usage = CpuUsageCalculator.Compute(null, current);

        Assert.Equal(0d, usage.TotalPercent);
        Assert.Equal(2, usage.PerCorePercent.Count);
        Assert.All(usage.PerCorePercent, value => Assert.Equal(0d, value));
    }

    [Fact]
    public void SecondSampleUsesJiffieDelta()
    {
        var previous = new ProcStatSample
        {
            Total = new ProcCpuTimes(100, 0, 100, 800),
            PerCore = new[] { new ProcCpuTimes(100, 0, 100, 800) },
        };
        var current = new ProcStatSample
        {
            Total = new ProcCpuTimes(200, 0, 200, 1600),
            PerCore = new[] { new ProcCpuTimes(200, 0, 200, 1600) },
        };

        var usage = CpuUsageCalculator.Compute(previous, current);

        Assert.Equal(20d, usage.TotalPercent, 6);
        Assert.Single(usage.PerCorePercent);
        Assert.Equal(20d, usage.PerCorePercent[0], 6);
    }

    [Fact]
    public void IowaitCountsAsIdleTime()
    {
        var previous = new ProcStatSample { Total = new ProcCpuTimes(0, 0, 0, 0, 0) };
        var current = new ProcStatSample { Total = new ProcCpuTimes(0, 0, 0, 100, 100) };

        var usage = CpuUsageCalculator.Compute(previous, current);

        Assert.Equal(0d, usage.TotalPercent);
    }

    [Fact]
    public void IdenticalSamplesReportZero()
    {
        var sample = new ProcStatSample { Total = new ProcCpuTimes(1, 2, 3, 4) };

        var usage = CpuUsageCalculator.Compute(sample, sample);

        Assert.Equal(0d, usage.TotalPercent);
    }

    [Fact]
    public void CounterResetReportsZeroInsteadOfNegativeOrOverflow()
    {
        var previous = new ProcStatSample { Total = new ProcCpuTimes(1000, 0, 1000, 8000) };
        var current = new ProcStatSample { Total = new ProcCpuTimes(10, 0, 10, 80) };

        var usage = CpuUsageCalculator.Compute(previous, current);

        Assert.Equal(0d, usage.TotalPercent);
    }

    [Fact]
    public void MissingCurrentSampleReportsEmptyUsage()
    {
        var usage = CpuUsageCalculator.Compute(null, null);

        Assert.Equal(0d, usage.TotalPercent);
        Assert.Empty(usage.PerCorePercent);
    }

    [Fact]
    public void PerCoreListFollowsTheCurrentSample()
    {
        var previous = new ProcStatSample
        {
            Total = new ProcCpuTimes(0, 0, 0, 0),
            PerCore = new[] { new ProcCpuTimes(0, 0, 0, 0), new ProcCpuTimes(0, 0, 0, 0) },
        };
        var current = new ProcStatSample
        {
            Total = new ProcCpuTimes(100, 0, 0, 0),
            PerCore = new[] { new ProcCpuTimes(100, 0, 0, 0) },
        };

        var usage = CpuUsageCalculator.Compute(previous, current);

        Assert.Single(usage.PerCorePercent);
        Assert.Equal(100d, usage.PerCorePercent[0], 6);
    }
}
