using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.App.Services;
using SurgeLink.App.Sessions;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Core.Tests;

public sealed class TerminalBridgeResponseTests
{
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
