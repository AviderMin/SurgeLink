using System.Runtime.CompilerServices;

namespace SurgeLink.Core.Monitoring;

/// <summary>A demand-driven sampling loop shared by every view of one session monitor.</summary>
public sealed class RemoteMonitorSampler
{
    private static readonly ConditionalWeakTable<IRemoteSystemMonitor, RemoteMonitorSampler> Samplers = new();
    private readonly IRemoteSystemMonitor _monitor;
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = new();
    private CancellationTokenSource? _lifetime;

    private RemoteMonitorSampler(IRemoteSystemMonitor monitor) => _monitor = monitor;

    public static RemoteMonitorSampler For(IRemoteSystemMonitor monitor) =>
        Samplers.GetValue(monitor, static target => new RemoteMonitorSampler(target));

    /// <summary>Subscribes until disposed; the last subscriber stops all remote work.</summary>
    public IDisposable Subscribe(TimeSpan interval, bool processes, Action<MonitorSample> receive, bool once = false)
    {
        ArgumentNullException.ThrowIfNull(receive);
        var subscription = new Subscription(this, interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : interval, processes, receive, once);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
            StartLocked();
        }
        return subscription;
    }

    private void StartLocked()
    {
        if (_lifetime is not null || _subscriptions.Count == 0) { return; }
        var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;
        _ = Task.Run(() => RunAsync(lifetime));
    }

    private async Task RunAsync(CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                Subscription[] due;
                TimeSpan delay;
                lock (_gate)
                {
                    var now = DateTimeOffset.UtcNow;
                    due = _subscriptions.Where(item => item.Next <= now).ToArray();
                    delay = _subscriptions.Count == 0 ? TimeSpan.Zero : _subscriptions.Min(item => item.Next) - now;
                }
                if (due.Length == 0)
                {
                    // Bound the delay so a newly mounted view can request a sample promptly.
                    await Task.Delay(delay > TimeSpan.Zero && delay < TimeSpan.FromSeconds(1) ? delay : TimeSpan.FromSeconds(1), lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                MonitorSample result;
                try
                {
                    var snapshot = await _monitor.SampleAsync(lifetime.Token).ConfigureAwait(false);
                    IReadOnlyList<RemoteProcessInfo> processes = Array.Empty<RemoteProcessInfo>();
                    bool needsProcesses;
                    lock (_gate) { needsProcesses = _subscriptions.Any(item => item.Processes && item.Next <= DateTimeOffset.UtcNow); }
                    if (needsProcesses)
                    {
                        try { processes = await _monitor.GetTopProcessesAsync(15, lifetime.Token).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is not OperationCanceledException) { }
                    }
                    result = new MonitorSample(snapshot, processes, null);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (Exception exception) { result = new MonitorSample(null, Array.Empty<RemoteProcessInfo>(), exception.Message); }
                lock (_gate)
                {
                    // Views mounted during a sample share its result as well.
                    due = _subscriptions.Where(item => item.Next <= DateTimeOffset.UtcNow).ToArray();
                }
                var nextFrom = DateTimeOffset.UtcNow;
                foreach (var item in due)
                {
                    lock (_gate)
                    {
                        if (!_subscriptions.Contains(item)) { continue; }
                        item.Next = nextFrom + item.Interval;
                        try { item.Receive(result); }
                        catch (Exception) { /* One unmounted view cannot stop another view's samples. */ }
                        if (item.Once) { RemoveLocked(item); }
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_lifetime, lifetime)) { _lifetime = null; }
                lifetime.Dispose();
                StartLocked();
            }
        }
    }

    private void RemoveLocked(Subscription subscription)
    {
        _subscriptions.Remove(subscription);
        if (_subscriptions.Count == 0) { _lifetime?.Cancel(); }
    }

    private sealed class Subscription(RemoteMonitorSampler owner, TimeSpan interval, bool processes, Action<MonitorSample> receive, bool once) : IDisposable
    {
        public TimeSpan Interval { get; } = interval;
        public bool Processes { get; } = processes;
        public Action<MonitorSample> Receive { get; } = receive;
        public bool Once { get; } = once;
        public DateTimeOffset Next { get; set; } = DateTimeOffset.MinValue;
        public void Dispose() { lock (owner._gate) { owner.RemoveLocked(this); } }
    }
}

/// <summary>One shared result, with an optional process table.</summary>
public sealed record MonitorSample(SystemSnapshot? Snapshot, IReadOnlyList<RemoteProcessInfo> Processes, string? Error);
