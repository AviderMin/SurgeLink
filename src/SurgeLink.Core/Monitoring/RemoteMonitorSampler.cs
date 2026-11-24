using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace SurgeLink.Core.Monitoring;

/// <summary>A demand-driven sampling loop shared by every view of one session monitor.</summary>
public sealed class RemoteMonitorSampler
{
    private static readonly ConditionalWeakTable<IRemoteSystemMonitor, RemoteMonitorSampler> Samplers = new();
    private readonly IRemoteSystemMonitor _monitor;
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private CancellationTokenSource? _lifetime;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private RemoteMonitorSampler(IRemoteSystemMonitor monitor) => _monitor = monitor;

    public static RemoteMonitorSampler For(IRemoteSystemMonitor monitor) =>
        Samplers.GetValue(monitor, static target => new RemoteMonitorSampler(target));

    /// <summary>Subscribes until disposed; the last subscriber stops all remote work.
    /// Disposal suppresses queued notifications; a callback already claimed may finish.</summary>
    public IDisposable Subscribe(TimeSpan interval, bool processes, Action<MonitorSample> receive, bool once = false)
    {
        ArgumentNullException.ThrowIfNull(receive);
        var subscription = new Subscription(this, interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : interval, processes, receive, once);
        TaskCompletionSource wake;
        lock (_gate)
        {
            _subscriptions.Add(subscription);
            StartLocked();
            wake = _wake;
        }
        wake.TrySetResult();
        return subscription;
    }

    private void StartLocked()
    {
        if (_lifetime is not null || _subscriptions.Count == 0) { return; }
        var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;
        _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                Task wakeTask;
                lock (_gate)
                {
                    var now = _clock.Elapsed;
                    due = _subscriptions.Where(item => item.Next <= now).ToArray();
                    delay = _subscriptions.Count == 0 ? TimeSpan.Zero : _subscriptions.Min(item => item.Next) - now;
                    if (due.Length == 0 && _wake.Task.IsCompleted)
                    {
                        _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    wakeTask = _wake.Task;
                }
                if (due.Length == 0)
                {
                    // Wake immediately when a new subscriber registers instead of waiting up to 1s.
                    var sleep = delay > TimeSpan.Zero && delay < TimeSpan.FromSeconds(1) ? delay : TimeSpan.FromSeconds(1);
                    await Task.WhenAny(Task.Delay(sleep, lifetime.Token), wakeTask).ConfigureAwait(false);
                    lifetime.Token.ThrowIfCancellationRequested();
                    continue;
                }
                MonitorSample result;
                try
                {
                    var snapshot = await _monitor.SampleAsync(lifetime.Token).ConfigureAwait(false);
                    IReadOnlyList<RemoteProcessInfo> processes = Array.Empty<RemoteProcessInfo>();
                    bool needsProcesses;
                    lock (_gate)
                    {
                        var now = _clock.Elapsed;
                        var dueIntervals = _subscriptions.Where(item => item.Next <= now).Select(item => item.Interval).ToHashSet();
                        needsProcesses = _subscriptions.Any(item => item.Processes && (item.Next <= now || (!item.Once && dueIntervals.Contains(item.Interval))));
                    }
                    if (needsProcesses)
                    {
                        try { processes = await _monitor.GetTopProcessesAsync(15, lifetime.Token).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is not OperationCanceledException) { }
                    }
                    result = new MonitorSample(snapshot, processes, null);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (Exception exception) { result = new MonitorSample(null, Array.Empty<RemoteProcessInfo>(), exception.Message); }
                CancellationTokenSource? stop = null;
                lock (_gate)
                {
                    if (lifetime.IsCancellationRequested) { return; }
                    // Views mounted during a sample or sharing the exact same cadence
                    // lock into phase so SidebarMonitor and MonitorView do not double-poll.
                    var completed = _clock.Elapsed;
                    var dueIntervals = _subscriptions.Where(item => item.Next <= completed).Select(item => item.Interval).ToHashSet();
                    due = _subscriptions.Where(item => item.Next <= completed || (!item.Once && dueIntervals.Contains(item.Interval))).ToArray();
                    foreach (var item in due)
                    {
                        // Reserve delivery before leaving the scheduling lock.
                        item.Next = TimeSpan.MaxValue;
                        if (item.Once) { stop = RemoveLocked(item) ?? stop; }
                    }
                }
                CancelOutsideLock(stop);
                foreach (var item in due)
                {
                    // A slow or reentrant consumer never owns the global gate.
                    item.Notify(result);
                }
                lock (_gate)
                {
                    // Completion includes optional process queries and delivery.
                    // Never catch up missed ticks or spend the interval in a callback.
                    var completed = _clock.Elapsed;
                    foreach (var item in due)
                    {
                        if (!_subscriptions.Contains(item)) { continue; }
                        item.Next = completed + item.Interval;
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

    private CancellationTokenSource? RemoveLocked(Subscription subscription)
    {
        return _subscriptions.Remove(subscription) && _subscriptions.Count == 0 ? _lifetime : null;
    }

    private static void CancelOutsideLock(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) { return; }
        try
        {
            // Protocol cancellation registrations are external callbacks too.
            // Run them asynchronously and never while holding the scheduler gate.
            var cancelled = cancellation.CancelAsync();
            _ = cancelled.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (ObjectDisposedException) { /* The worker already completed. */ }
    }

    private sealed class Subscription(RemoteMonitorSampler owner, TimeSpan interval, bool processes, Action<MonitorSample> receive, bool once) : IDisposable
    {
        // 0 = available, 1 = notification claimed, 2 = disposed/completed.
        private int _state;
        public TimeSpan Interval { get; } = interval;
        public bool Processes { get; } = processes;
        public bool Once { get; } = once;
        public TimeSpan Next { get; set; }
        public void Notify(MonitorSample result)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) { return; }
            try { receive(result); }
            catch (Exception) { /* One consumer cannot stop other subscriptions. */ }
            finally { Interlocked.CompareExchange(ref _state, Once ? 2 : 0, 1); }
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref _state, 2);
            CancellationTokenSource? stop;
            lock (owner._gate) { stop = owner.RemoveLocked(this); }
            CancelOutsideLock(stop);
        }
    }
}

/// <summary>One shared result, with an optional process table.</summary>
public sealed record MonitorSample(SystemSnapshot? Snapshot, IReadOnlyList<RemoteProcessInfo> Processes, string? Error);
