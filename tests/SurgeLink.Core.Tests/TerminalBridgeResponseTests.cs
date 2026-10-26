using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.App.Services;
using SurgeLink.App.Sessions;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Core.Tests;

public sealed class TerminalBridgeResponseTests
{
    [Fact]
    public async Task SavedHistoryLimitAppliesToNewAndRunningTerminalsAndUnsubscribesOnClose()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettingsStore(temp.File("settings.json"));
        settings.Update(s => s with { TerminalScrollbackLines = 1000 });
        var remote = new CapturingTerminal();
        var bridge = new TerminalBridge(remote, new InlineDispatcher(), NullLogger.Instance, settings: settings);
        remote.Emit(string.Concat(Enumerable.Repeat("line\r\n", 1500)));
        Assert.Equal(1000, bridge.Emulator.ScrollbackLimit);
        Assert.Equal(1000, bridge.Emulator.ScrollbackLength);
        settings.Update(s => s with { TerminalScrollbackLines = 5000 });
        remote.Emit(string.Concat(Enumerable.Repeat("line\r\n", 1500)));
        Assert.Equal(5000, bridge.Emulator.ScrollbackLimit);
        Assert.True(bridge.Emulator.ScrollbackLength > 1000);
        settings.Update(s => s with { TerminalScrollbackLines = 1000 });
        Assert.Equal(1000, bridge.Emulator.ScrollbackLength);
        var invalidations = 0;
        bridge.Invalidated += (_, _) => invalidations++;
        await bridge.DisposeAsync();
        settings.Update(s => s with { TerminalScrollbackLines = 5000 });
        Assert.Equal(0, invalidations);
        Assert.Equal(1000, bridge.Emulator.ScrollbackLimit);
    }

    [Fact]
    public async Task RemoteQueryIsWrittenBackAndSubscriptionIsRemovedOnDispose()
    {
        var remote = new CapturingTerminal();
        var bridge = new TerminalBridge(remote, new InlineDispatcher(), NullLogger.Instance);
        await bridge.StartAsync();
        remote.Emit("\u001b[4;9H\u001b[6n\u001b[5n");
        Assert.Equal(new[] { "\u001b[4;9R", "\u001b[0n" }, remote.Writes);
        await bridge.DisposeAsync();
        bridge.Emulator.Feed(Encoding.ASCII.GetBytes("\u001b[6n"));
        remote.Emit("\u001b[6n");
        Assert.Equal(2, remote.Writes.Count);
    }

    [Fact]
    public async Task ConcurrentResizesDoNotLetAnOlderSizeOvertakeTheNewest()
    {
        var remote = new GatedResizeTerminal();
        await using var bridge = new TerminalBridge(remote, new InlineDispatcher(), NullLogger.Instance);

        // Start a wide resize and hold it inside the remote call.
        var first = bridge.ResizeAsync(100, 40);
        await remote.FirstResizeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A newer, narrower size arrives while the first one is still in flight.
        var second = bridge.ResizeAsync(80, 24);
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        remote.ReleaseFirstResize.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        // The first request may already be in flight, so it can be delivered.
        // The guarantee is that the newest size is the last one sent and that
        // no superseded size follows it.
        Assert.Equal((80, 24), remote.Sizes[^1]);
        Assert.Single(remote.Sizes, size => size == (100, 40));
        Assert.Equal(80, bridge.Emulator.Columns);
        Assert.Equal(24, bridge.Emulator.Rows);
    }

    [Fact]
    public async Task SupersededResizeWaitingForTheGateIsNotSent()
    {
        var remote = new GatedResizeTerminal();
        await using var bridge = new TerminalBridge(remote, new InlineDispatcher(), NullLogger.Instance);

        // The first resize occupies the remote call.
        var inFlight = bridge.ResizeAsync(100, 40);
        await remote.FirstResizeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Two more requests only wait on the gate; the middle one is stale.
        var stale = bridge.ResizeAsync(90, 30);
        var newest = bridge.ResizeAsync(80, 24);
        await Task.Delay(50);

        remote.ReleaseFirstResize.TrySetResult();
        await Task.WhenAll(inFlight, stale, newest).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain((90, 30), remote.Sizes);
        Assert.Equal((80, 24), remote.Sizes[^1]);
    }
    private sealed class GatedResizeTerminal : IRemoteTerminal
    {
        private readonly object _gate = new();
        private readonly List<(int Columns, int Rows)> _sizes = [];
        public TaskCompletionSource FirstResizeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstResize { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<(int Columns, int Rows)> Sizes { get { lock (_gate) return _sizes.ToArray(); } }
        public int Columns => 120;
        public int Rows => 32;
        public bool IsOpen => true;
        public event EventHandler<TerminalOutputEventArgs>? OutputReceived { add { } remove { } }
        public event EventHandler? Closed { add { } remove { } }
        public Task StartAsync(TerminalStartOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public async Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            lock (_gate) { _sizes.Add((columns, rows)); }
            if (columns == 100)
            {
                FirstResizeEntered.TrySetResult();
                await ReleaseFirstResize.Task.WaitAsync(cancellationToken);
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool IsOnUiThread => true;
        public void Post(Action action) => action();
    }

    private sealed class CapturingTerminal : IRemoteTerminal
    {
        public int Columns => 120;
        public int Rows => 32;
        public bool IsOpen => true;
        public List<string> Writes { get; } = [];
        public event EventHandler<TerminalOutputEventArgs>? OutputReceived;
        public event EventHandler? Closed { add { } remove { } }
        public void Emit(string text) => OutputReceived?.Invoke(this, new TerminalOutputEventArgs(Encoding.ASCII.GetBytes(text)));
        public Task StartAsync(TerminalStartOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Writes.Add(Encoding.ASCII.GetString(data.Span));
            return ValueTask.CompletedTask;
        }
        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
