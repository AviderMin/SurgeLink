using SurgeLink.App.Views.Monitor;
using SurgeLink.Core.Monitoring;
using Xunit;

namespace SurgeLink.Core.Tests;

public sealed class MonitorHistoryTests
{
    [Fact]
    public void NetworkRatesAreDerivedPerInterfaceAcrossSingleCounterReset()
    {
        var start = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        var history = new MonitorHistory();

        history.Add(Snapshot(start, ("eth0", 100, 100), ("eth1", 100, 100)));
        history.Add(Snapshot(start.AddSeconds(1), ("eth0", 0, 0), ("eth1", 250, 350)));

        Assert.Equal(150d, history.ReceiveRate, 6);
        Assert.Equal(250d, history.SendRate, 6);
    }

    [Fact]
    public void NewAndRemovedInterfacesDoNotDistortRates()
    {
        var start = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        var history = new MonitorHistory();

        history.Add(Snapshot(start, ("eth0", 100, 100)));
        history.Add(Snapshot(start.AddSeconds(1), ("eth0", 200, 200), ("eth1", 1000, 1000)));
        history.Add(Snapshot(start.AddSeconds(2), ("eth0", 300, 300)));

        Assert.Equal(100d, history.ReceiveRate, 6);
        Assert.Equal(100d, history.SendRate, 6);
    }

    [Fact]
    public void ReceiveResetDoesNotHideSendDelta()
    {
        var start = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        var history = new MonitorHistory();

        history.Add(Snapshot(start, ("eth0", 100, 100)));
        history.Add(Snapshot(start.AddSeconds(1), ("eth0", 0, 300)));

        Assert.Equal(0d, history.ReceiveRate, 6);
        Assert.Equal(200d, history.SendRate, 6);
    }

    [Fact]
    public void ReappearingInterfaceStartsAFreshBaseline()
    {
        var start = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        var history = new MonitorHistory();

        history.Add(Snapshot(start, ("eth0", 100, 100), ("eth1", 100, 100)));
        history.Add(Snapshot(start.AddSeconds(1), ("eth1", 200, 200)));
        history.Add(Snapshot(start.AddSeconds(2), ("eth0", 5000, 5000), ("eth1", 300, 300)));

        Assert.Equal(100d, history.ReceiveRate, 6);
        Assert.Equal(100d, history.SendRate, 6);
    }

    [Fact]
    public void InterfaceNamesAreMatchedCaseSensitively()
    {
        var start = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        var history = new MonitorHistory();

        history.Add(Snapshot(start, ("eth0", 100, 100)));
        history.Add(Snapshot(start.AddSeconds(1), ("ETH0", 1100, 1100)));

        Assert.Equal(0d, history.ReceiveRate, 6);
        Assert.Equal(0d, history.SendRate, 6);
    }

    [Fact]
    public void HistoryPropertiesReturnIndependentSnapshots()
    {
        var start = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        var history = new MonitorHistory();

        history.Add(Snapshot(start, ("eth0", 0, 0)));
        var received = history.ReceiveHistory;
        var cpu = history.CpuHistory;

        history.Add(Snapshot(start.AddSeconds(1), ("eth0", 100, 100)));

        Assert.Single(received);
        Assert.Single(cpu);
        Assert.Equal(2, history.ReceiveHistory.Count);
        Assert.Equal(100d, history.ReceiveHistory[1], 6);
    }

    [Fact]
    public async Task AddIsSafeUnderConcurrentWriters()
    {
        var start = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        var history = new MonitorHistory();

        var writers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 250; i++)
            {
                var bytes = i * 10L;
                history.Add(Snapshot(
                    start.AddMilliseconds(i),
                    ($"eth{worker}", bytes, bytes)));
            }
        }));

        await Task.WhenAll(writers);

        Assert.Equal(MonitorHistory.Capacity, history.CpuHistory.Count);
        Assert.Equal(MonitorHistory.Capacity, history.ReceiveHistory.Count);
    }

    private static SystemSnapshot Snapshot(
        DateTimeOffset timestamp,
        params (string Name, long Received, long Sent)[] networks) =>
        new()
        {
            TimestampUtc = timestamp,
            Networks = networks
                .Select(network => new NetworkSample
                {
                    InterfaceName = network.Name,
                    BytesReceived = network.Received,
                    BytesSent = network.Sent,
                })
                .ToArray(),
        };
}
