using SurgeLink.Core.Monitoring;

namespace SurgeLink.Protocols.Conformance;

public sealed class MonitorConformanceTests
{
    [Theory]
    [MemberData(nameof(ProtocolConformanceData.MonitorFixtureNames), MemberType = typeof(ProtocolConformanceData))]
    public async Task MonitorReturnsSaneSamplesAndProcesses(string fixtureName)
    {
        IProtocolUnderTest fixture = ProtocolConformanceRegistry.Get(fixtureName);
        TimeSpan timeout = ProtocolConformanceSuite.DefaultOperationTimeout;

        await using ConformanceSession connection = await ConformanceSession.OpenAsync(fixture);
        IRemoteSystemMonitor monitor = connection.Session.Monitor!;

        Assert.True(monitor.RecommendedInterval > TimeSpan.Zero, "recommended sample interval must be positive");

        SystemSnapshot first = await ConformanceAssert.CompletesWithinAsync(
            monitor.SampleAsync(CancellationToken.None),
            timeout,
            "first SampleAsync");
        ConformanceAssert.AssertSaneSnapshot(first);

        SystemSnapshot second = await ConformanceAssert.CompletesWithinAsync(
            monitor.SampleAsync(CancellationToken.None),
            timeout,
            "second SampleAsync");
        ConformanceAssert.AssertSaneSnapshot(second);
        Assert.True(second.TimestampUtc >= first.TimestampUtc, "consecutive samples must not report time going backwards");

        IReadOnlyList<RemoteProcessInfo> processes = await ConformanceAssert.CompletesWithinAsync(
            monitor.GetTopProcessesAsync(3, CancellationToken.None),
            timeout,
            "GetTopProcessesAsync");

        Assert.NotNull(processes);
        Assert.True(processes.Count <= 3, "top processes must respect maxCount");
        foreach (RemoteProcessInfo process in processes)
        {
            Assert.True(process.ProcessId > 0, "process id must be positive");
            Assert.False(string.IsNullOrWhiteSpace(process.Name), "process name must be set");
            Assert.InRange(process.CpuPercent, 0d, 100d);
            Assert.InRange(process.MemoryPercent, 0d, 100d);
            Assert.True(process.ResidentBytes >= 0, "resident bytes must not be negative");
        }
    }
}
