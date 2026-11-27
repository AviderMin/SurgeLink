using SurgeLink.App.Services;
using SurgeLink.Core.Monitoring;

namespace SurgeLink.App.Views.Monitor;

/// <summary>Props for <see cref="MonitorView"/>.</summary>
/// <param name="Monitor">Performance monitor of the session; <c>null</c> when the protocol has none.</param>
/// <param name="Settings">User preferences; supplies the sampling interval.</param>
internal sealed record MonitorViewProps(IRemoteSystemMonitor? Monitor, IAppSettingsStore Settings, bool IsActive = true);

/// <summary>Session-owned display state survives native TabView content remounts.</summary>
internal sealed class MonitorViewState
{
    public MonitorHistory History { get; } = new();
    public bool Paused { get; set; }
    public SystemSnapshot? Snapshot { get; set; }
    public IReadOnlyList<RemoteProcessInfo> Processes { get; set; } = Array.Empty<RemoteProcessInfo>();
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? Error { get; set; }
}

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
    private readonly Dictionary<string, (long Received, long Sent)> _lastNetworks = new(StringComparer.Ordinal);
    private double _receiveRate;
    private double _sendRate;

    /// <summary>Latest download rate in bytes per second.</summary>
    public double ReceiveRate { get { lock (_gate) return _receiveRate; } }

    /// <summary>Latest upload rate in bytes per second.</summary>
    public double SendRate { get { lock (_gate) return _sendRate; } }

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
        lock (_gate)
        {
            var seconds = _lastTimestamp is { } previous
                ? (snapshot.TimestampUtc - previous).TotalSeconds
                : 0d;

            // Keep one baseline per interface. Summing all counters first loses
            // information when one adapter resets while another keeps moving,
            // and treats interfaces that appear or disappear as traffic spikes.
            var receivedDelta = 0L;
            var sentDelta = 0L;
            var current = new Dictionary<string, (long Received, long Sent)>(
                snapshot.Networks.Count,
                StringComparer.Ordinal);
            foreach (var network in snapshot.Networks)
            {
                current[network.InterfaceName] = (network.BytesReceived, network.BytesSent);
                if (_lastNetworks.TryGetValue(network.InterfaceName, out var last))
                {
                    if (network.BytesReceived >= last.Received)
                    {
                        receivedDelta += network.BytesReceived - last.Received;
                    }

                    if (network.BytesSent >= last.Sent)
                    {
                        sentDelta += network.BytesSent - last.Sent;
                    }
                }
            }

            if (_lastTimestamp is not null && seconds > 0.05)
            {
                _receiveRate = receivedDelta / seconds;
                _sendRate = sentDelta / seconds;
            }
            else
            {
                _receiveRate = 0d;
                _sendRate = 0d;
            }

            _lastTimestamp = snapshot.TimestampUtc;
            _lastNetworks.Clear();
            foreach (var pair in current)
            {
                _lastNetworks[pair.Key] = pair.Value;
            }

            Append(_cpu, snapshot.Cpu.TotalPercent);
            Append(_memory, snapshot.Memory.UsedFraction * 100);
            Append(_received, _receiveRate);
            Append(_sent, _sendRate);
        }
    }

    private static void Append(List<double> values, double value)
    {
        values.Add(double.IsFinite(value) ? value : 0d);
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
