using SurgeLink.Core.Monitoring;

namespace SurgeLink.Core.Tests;

public sealed class RemoteMonitorSamplerTests
{
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
}
