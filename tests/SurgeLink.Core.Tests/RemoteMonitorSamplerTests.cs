using SurgeLink.Core.Monitoring;
using System.Diagnostics;

namespace SurgeLink.Core.Tests;

public sealed class RemoteMonitorSamplerTests
{
    [Fact]
    public async Task SeparateMonitorInstancesCannotDeliverEachOthersSamples()
    {
        var firstMonitor = new IdentifiedMonitor(11);
        var secondMonitor = new IdentifiedMonitor(22);
        var first = new TaskCompletionSource<MonitorSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<MonitorSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.NotSame(RemoteMonitorSampler.For(firstMonitor), RemoteMonitorSampler.For(secondMonitor));
        using var a = RemoteMonitorSampler.For(firstMonitor).Subscribe(TimeSpan.FromSeconds(1), false, s => first.TrySetResult(s), once: true);
        using var b = RemoteMonitorSampler.For(secondMonitor).Subscribe(TimeSpan.FromSeconds(1), false, s => second.TrySetResult(s), once: true);
        Assert.Equal(11, (await first.Task.WaitAsync(TimeSpan.FromSeconds(5))).Snapshot!.Cpu.TotalPercent);
        Assert.Equal(22, (await second.Task.WaitAsync(TimeSpan.FromSeconds(5))).Snapshot!.Cpu.TotalPercent);
    }

    private sealed class IdentifiedMonitor(double identity) : IRemoteSystemMonitor
    {
        public TimeSpan RecommendedInterval => TimeSpan.FromSeconds(1);
        public Task<SystemSnapshot> SampleAsync(CancellationToken ct) => Task.FromResult(new SystemSnapshot { Cpu = new CpuSample { TotalPercent = identity } });
        public Task<IReadOnlyList<RemoteProcessInfo>> GetTopProcessesAsync(int count, CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteProcessInfo>>([]);
    }
    [Fact]
    public async Task SlowCallbackDoesNotBlockSubscriptionOrDisposal()
    {
        var sampler = RemoteMonitorSampler.For(new ImmediateMonitor());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var first = sampler.Subscribe(TimeSpan.FromSeconds(30), false, _ =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var registering = Task.Run(() => sampler.Subscribe(TimeSpan.FromSeconds(30), false, _ => { }));
        try
        {
            using var second = await registering.WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Run(first.Dispose).WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            release.Set();
            (await registering.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingDuringAnotherCallbackSuppressesPendingDelivery(bool once)
    {
        var monitor = new FakeMonitor();
        var sampler = RemoteMonitorSampler.For(monitor);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveredLast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var first = sampler.Subscribe(TimeSpan.FromSeconds(30), false, _ =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
        });
        var calls = 0;
        using var pending = sampler.Subscribe(TimeSpan.FromSeconds(30), false, _ => Interlocked.Increment(ref calls), once);
        using var last = sampler.Subscribe(TimeSpan.FromSeconds(30), false, _ => deliveredLast.TrySetResult(), once: true);
        await monitor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        monitor.Release.TrySetResult();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(pending.Dispose).WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally { release.Set(); }
        await deliveredLast.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task OneShotCallbackCanRegisterTheNextOneShotWithoutLosingIt()
    {
        var sampler = RemoteMonitorSampler.For(new ImmediateMonitor());
        var nextResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? next = null;
        using var first = sampler.Subscribe(TimeSpan.FromSeconds(1), false, _ =>
        {
            next = sampler.Subscribe(TimeSpan.FromSeconds(1), false, _ => nextResult.TrySetResult(), once: true);
        }, once: true);
        await nextResult.Task.WaitAsync(TimeSpan.FromSeconds(5));
        next?.Dispose();
    }

    [Fact]
    public async Task ProtocolCancellationHooksCannotBlockTheSchedulerGate()
    {
        using var release = new ManualResetEventSlim();
        var monitor = new CancellationMonitor(release);
        var sampler = RemoteMonitorSampler.For(monitor);
        using var first = sampler.Subscribe(TimeSpan.FromSeconds(1), false, _ => { });
        await monitor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<IDisposable>? registering = null;
        try
        {
            await Task.Run(first.Dispose).WaitAsync(TimeSpan.FromSeconds(1));
            await monitor.Cancelling.Task.WaitAsync(TimeSpan.FromSeconds(5));
            registering = Task.Run(() => sampler.Subscribe(TimeSpan.FromSeconds(1), false, _ => { }));
            using var second = await registering.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            release.Set();
            if (registering is not null) { (await registering.WaitAsync(TimeSpan.FromSeconds(5))).Dispose(); }
        }
    }

    [Fact]
    public async Task CallbackFailureDoesNotStopTheRemainingNotifications()
    {
        var monitor = new FakeMonitor();
        var sampler = RemoteMonitorSampler.For(monitor);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = sampler.Subscribe(TimeSpan.FromSeconds(1), false, _ => throw new InvalidOperationException("view failed"), once: true);
        using var second = sampler.Subscribe(TimeSpan.FromSeconds(1), false, _ => received.TrySetResult(), once: true);
        await monitor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        monitor.Release.TrySetResult();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, monitor.Calls);
    }

    [Fact]
    public async Task SlowSnapshotAndProcessQueryWaitTheFullIntervalBeforeSamplingAgain()
    {
        var monitor = new SlowMonitor();
        var sampler = RemoteMonitorSampler.For(monitor);
        using var dashboard = sampler.Subscribe(TimeSpan.FromSeconds(1), true, _ => { });
        var slowDeliveries = 0;
        using var sidebar = sampler.Subscribe(TimeSpan.FromSeconds(30), false, _ => Interlocked.Increment(ref slowDeliveries));
        var secondStart = await monitor.SecondStart.Task.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.True(Stopwatch.GetElapsedTime(monitor.ProcessCompleted, secondStart) >= TimeSpan.FromMilliseconds(950));
        Assert.Equal(1, Volatile.Read(ref slowDeliveries));
        Assert.Equal(2, monitor.Calls);
    }

    [Fact]
    public async Task ViewsShareOneSampleAndLastUnsubscribeCancelsInFlightWork()
    {
        var monitor = new FakeMonitor();
        var sampler = RemoteMonitorSampler.For(monitor);
        Assert.Same(sampler, RemoteMonitorSampler.For(monitor));
        var first = new TaskCompletionSource<MonitorSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<MonitorSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sidebar = sampler.Subscribe(TimeSpan.FromSeconds(1), false, sample => first.TrySetResult(sample));
        using var dashboard = sampler.Subscribe(TimeSpan.FromSeconds(1), true, sample => second.TrySetResult(sample));
        await monitor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        monitor.Release.TrySetResult();
        var samples = await Task.WhenAll(first.Task, second.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(samples[0].Snapshot, samples[1].Snapshot);
        Assert.Equal(1, monitor.Calls);
        await monitor.NextEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sidebar.Dispose();
        dashboard.Dispose();
        await monitor.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FakeMonitor : IRemoteSystemMonitor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NextEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public TimeSpan RecommendedInterval => TimeSpan.FromSeconds(1);
        public async Task<SystemSnapshot> SampleAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref Calls);
            if (count == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                return new SystemSnapshot();
            }
            NextEntered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            return new SystemSnapshot();
        }
        public Task<IReadOnlyList<RemoteProcessInfo>> GetTopProcessesAsync(int maxCount, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteProcessInfo>>([]);
    }

    private sealed class ImmediateMonitor : IRemoteSystemMonitor
    {
        public TimeSpan RecommendedInterval => TimeSpan.FromSeconds(1);
        public Task<SystemSnapshot> SampleAsync(CancellationToken cancellationToken) => Task.FromResult(new SystemSnapshot());
        public Task<IReadOnlyList<RemoteProcessInfo>> GetTopProcessesAsync(int maxCount, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteProcessInfo>>([]);
    }

    private sealed class SlowMonitor : IRemoteSystemMonitor
    {
        public int Calls;
        public long ProcessCompleted;
        public TaskCompletionSource<long> SecondStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan RecommendedInterval => TimeSpan.FromSeconds(1);
        public async Task<SystemSnapshot> SampleAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Calls) == 2)
            {
                SecondStart.TrySetResult(Stopwatch.GetTimestamp());
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            await Task.Delay(1200, cancellationToken);
            return new SystemSnapshot();
        }
        public async Task<IReadOnlyList<RemoteProcessInfo>> GetTopProcessesAsync(int maxCount, CancellationToken cancellationToken)
        {
            await Task.Delay(300, cancellationToken);
            ProcessCompleted = Stopwatch.GetTimestamp();
            return [];
        }
    }

    private sealed class CancellationMonitor(ManualResetEventSlim release) : IRemoteSystemMonitor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelling { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan RecommendedInterval => TimeSpan.FromSeconds(1);
        public async Task<SystemSnapshot> SampleAsync(CancellationToken cancellationToken)
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() =>
            {
                Cancelling.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(5));
                cancelled.TrySetResult();
            });
            Started.TrySetResult();
            await cancelled.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return new SystemSnapshot();
        }
        public Task<IReadOnlyList<RemoteProcessInfo>> GetTopProcessesAsync(int maxCount, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteProcessInfo>>([]);
    }
}
