using SurgeLink.App.Services;
using SurgeLink.Core.Monitoring;

namespace SurgeLink.App.Views.Monitor;

/// <summary>Props for <see cref="MonitorView"/>.</summary>
/// <param name="Monitor">Performance monitor of the session; <c>null</c> when the protocol has none.</param>
/// <param name="Settings">User preferences; supplies the sampling interval.</param>
internal sealed record MonitorViewProps(IRemoteSystemMonitor? Monitor, IAppSettingsStore Settings);

/// <summary>
/// Rolling history behind the charts. Network rates are derived from the
/// cumulative counters by dividing the byte delta by the elapsed time between
/// two samples; counters that go backwards (interface reset) report 0 for that
/// interval instead of a negative rate.
/// </summary>
internal sealed class MonitorHistory
{
    /// <summary>Number of samples kept (the chart window).</summary>
    public const int Capacity = 60;

    private readonly object _gate = new();
    private readonly List<double> _cpu = new(Capacity);
    private readonly List<double> _memory = new(Capacity);
    private readonly List<double> _received = new(Capacity);
    private readonly List<double> _sent = new(Capacity);
    private DateTimeOffset? _lastTimestamp;
    private long _lastReceived;
    private long _lastSent;

    /// <summary>Latest download rate in bytes per second.</summary>
    public double ReceiveRate { get; private set; }

    /// <summary>Latest upload rate in bytes per second.</summary>
    public double SendRate { get; private set; }

    /// <summary>CPU percentage history, oldest first.</summary>
    public IReadOnlyList<double> CpuHistory => Snapshot(_cpu);

    /// <summary>Memory used fraction (0..100) history, oldest first.</summary>
    public IReadOnlyList<double> MemoryHistory => Snapshot(_memory);

    /// <summary>Download rate history in bytes per second, oldest first.</summary>
    public IReadOnlyList<double> ReceiveHistory => Snapshot(_received);

    /// <summary>Upload rate history in bytes per second, oldest first.</summary>
    public IReadOnlyList<double> SendHistory => Snapshot(_sent);

    /// <summary>Appends a sample and derives the throughput rates.</summary>
    public void Add(SystemSnapshot snapshot)
    {
        var received = 0L;
        var sent = 0L;
        foreach (var network in snapshot.Networks)
        {
            received += network.BytesReceived;
            sent += network.BytesSent;
        }

        lock (_gate)
        {
            var seconds = _lastTimestamp is { } previous
                ? (snapshot.TimestampUtc - previous).TotalSeconds
                : 0d;

            if (_lastTimestamp is not null && seconds > 0.05)
            {
                ReceiveRate = received >= _lastReceived ? (received - _lastReceived) / seconds : 0d;
                SendRate = sent >= _lastSent ? (sent - _lastSent) / seconds : 0d;
            }
            else
            {
                ReceiveRate = 0d;
                SendRate = 0d;
            }

            _lastTimestamp = snapshot.TimestampUtc;
            _lastReceived = received;
            _lastSent = sent;

            Append(_cpu, snapshot.Cpu.TotalPercent);
            Append(_memory, snapshot.Memory.UsedFraction * 100);
            Append(_received, ReceiveRate);
            Append(_sent, SendRate);
        }
    }

    private static void Append(List<double> values, double value)
    {
        values.Add(value);
        if (values.Count > Capacity)
        {
            values.RemoveAt(0);
        }
    }

    private IReadOnlyList<double> Snapshot(List<double> values)
    {
        lock (_gate)
        {
            return values.ToArray();
        }
    }
}
