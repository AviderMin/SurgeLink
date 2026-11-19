using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.App.Services;
using SurgeLink.App.Sessions;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Core.Tests;

/// <summary>
/// Shutdown must interrupt a blocked input write instead of waiting for the
/// transport to release it; otherwise closing a tab whose remote write is stuck
/// can hang indefinitely.
/// </summary>
public sealed class TerminalBridgeShutdownTests
{
    [Fact]
    public async Task DisposeDoesNotWaitForABlockedWrite()
    {
        var remote = new BlockingWriteTerminal();
        await using var bridge = new TerminalBridge(remote, new InlineDispatcher(), NullLogger.Instance);

        bridge.SendText("blocked");
        await remote.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task disposal = bridge.DisposeAsync().AsTask();
        Task completed = await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(disposal, completed);
        await disposal;
        Assert.True(remote.DisposeCalled);
    }

    [Fact]
    public async Task MultipleDisposeCallersShareOneTransportDispose()
    {
        var remote = new BlockingWriteTerminal();
        await using var bridge = new TerminalBridge(remote, new InlineDispatcher(), NullLogger.Instance);

        Task first = bridge.DisposeAsync().AsTask();
        Task second = bridge.DisposeAsync().AsTask();
        await Task.WhenAll(first, second);

        Assert.Equal(1, remote.DisposeCount);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool IsOnUiThread => true;
        public void Post(Action action) => action();
    }

    private sealed class BlockingWriteTerminal : IRemoteTerminal
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DisposeCalled { get; private set; }
        public int DisposeCount { get; private set; }

        public int Columns => 120;
        public int Rows => 32;
        public bool IsOpen => true;

        public event EventHandler<TerminalOutputEventArgs>? OutputReceived { add { } remove { } }
        public event EventHandler? Closed { add { } remove { } }

        public Task StartAsync(TerminalStartOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await _disposed.Task.ConfigureAwait(false);
        }

        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeCalled = true;
            _disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
