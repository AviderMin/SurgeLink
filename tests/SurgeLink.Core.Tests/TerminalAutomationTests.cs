using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.App.Services;
using SurgeLink.App.Sessions;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Core.Tests;

public sealed class TerminalAutomationTests
{
    [Fact]
    public async Task TextAndSubmitAreOneAwaitedWriteInTheVisiblePty()
    {
        var remote = new AutomationRemote();
        await using var bridge = Create(remote);
        await bridge.StartAsync();
        var humanEvents = 0;
        bridge.UserInput += (_, _) => humanEvents++;
        remote.Emit("\u001b[?2004h");
        var receipt = await bridge.SendTextAsync("echo 你好", true, bridge.ReadScreen().InputRevision, default);
        Assert.Equal("\u001b[200~echo 你好\u001b[201~\r", Assert.Single(remote.Writes));
        Assert.Equal(1, receipt.InputRevision);
        Assert.True(receipt.Submitted);
        Assert.Equal(0, humanEvents);
        remote.Emit("echo 你好\r\n你好\r\n$ ");
        Assert.Contains("你好", bridge.ReadScreen().Text);
        Assert.True(bridge.ReadScreen().BracketedPaste);
    }

    [Theory]
    [InlineData("Ctrl+C", "\u0003")]
    [InlineData("Shift+Tab", "\u001b[Z")]
    [InlineData("Up", "\u001bOA")]
    [InlineData("Enter", "\r")]
    public async Task KeysUseCurrentTerminalModes(string key, string expected)
    {
        var remote = new AutomationRemote();
        await using var bridge = Create(remote);
        remote.Emit("\u001b[?1h\u001b[?1049h");
        await bridge.SendKeyAsync(key, 0, default);
        Assert.Equal(expected, Assert.Single(remote.Writes));
        Assert.True(bridge.ReadScreen().AlternateScreen);
    }

    [Fact]
    public async Task HumanInputInvalidatesStaleObservationBeforeAnyAgentWrite()
    {
        var remote = new AutomationRemote();
        await using var bridge = Create(remote);
        var revision = bridge.ReadScreen().InputRevision;
        var writesAtTakeover = -1;
        bridge.UserInput += (_, _) => writesAtTakeover = remote.Writes.Count;
        bridge.SendText("human");
        Assert.Equal(0, writesAtTakeover);
        await Assert.ThrowsAsync<TerminalInputConflictException>(() => bridge.SendTextAsync("agent", true, revision, default));
        Assert.Equal("human", Assert.Single(remote.Writes));
    }

    [Fact]
    public async Task HumanAndAgentWritesCannotInterleave()
    {
        var remote = new AutomationRemote();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.BeforeWrite = (_, _) => new ValueTask(blocked.Task);
        await using var bridge = Create(remote);
        var first = bridge.SendTextAsync("agent", true, 0, default);
        bridge.SendText("human");
        Assert.Equal("agent\r", Assert.Single(remote.Writes));
        remote.BeforeWrite = null;
        blocked.SetResult();
        await first;
        // A new input queues behind the pending human write and lets the test await that queue.
        await bridge.SendKeyAsync("Enter", bridge.ReadScreen().InputRevision, default);
        Assert.Equal(new[] { "agent\r", "human", "\r" }, remote.Writes);
    }

    [Fact]
    public async Task FailedWriteReportsUncertainOutcomeAndAdvancesRevision()
    {
        var remote = new AutomationRemote { BeforeWrite = (_, _) => ValueTask.FromException(new IOException("disconnected after send")) };
        await using var bridge = Create(remote);
        await Assert.ThrowsAsync<TerminalInputUnknownException>(() => bridge.SendTextAsync("change", true, 0, default));
        Assert.Equal(1, bridge.ReadScreen().InputRevision);
        Assert.Single(remote.Writes);
    }

    [Fact]
    public async Task ReceiptCursorDoesNotSkipOutputReceivedDuringTheWrite()
    {
        var remote = new AutomationRemote();
        remote.BeforeWrite = (_, _) => { remote.Emit("fast reply"); return ValueTask.CompletedTask; };
        await using var bridge = Create(remote);
        var receipt = await bridge.SendTextAsync("command", true, 0, default);
        Assert.Equal(0, receipt.OutputCursor);
        Assert.Equal("fast reply", bridge.ReadOutput(receipt.OutputCursor).Text);
    }

    [Fact]
    public async Task AgentInputReturnsTheVisibleViewportToTheLiveTerminal()
    {
        var remote = new AutomationRemote();
        await using var bridge = Create(remote);
        remote.Emit(string.Concat(Enumerable.Repeat("line\r\n", 60)));
        lock (bridge.Emulator.SyncRoot) bridge.Emulator.ScrollViewport(10);
        Assert.True(bridge.ReadScreen().ViewportOffset > 0);
        await bridge.SendTextAsync("live input", false, 0, default);
        Assert.Equal(0, bridge.ReadScreen().ViewportOffset);
    }

    [Fact]
    public async Task CancelledQueuedInputDoesNotWriteOrAdvanceRevision()
    {
        var remote = new AutomationRemote();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.BeforeWrite = (_, _) => new ValueTask(blocked.Task);
        await using var bridge = Create(remote);
        var first = bridge.SendTextAsync("first", false, 0, default);
        using var cancellation = new CancellationTokenSource();
        var next = bridge.SendTextAsync("second", false, 1, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.Single(remote.Writes);
        blocked.SetResult();
        await first;
        Assert.Equal(1, bridge.ReadScreen().InputRevision);
    }

    [Fact]
    public void OutputDecodesUtf8AcrossReadsAndRetainsEscapeSequences()
    {
        var output = new TerminalOutputBuffer();
        var bytes = Encoding.UTF8.GetBytes("你好🙂\u001b[31mRED\u001b[0m");
        foreach (var value in bytes) output.Append(new[] { value });
        var first = output.Read(null, 3);
        Assert.Equal("你好", first.Text); // Do not split the emoji's surrogate pair at the page boundary.
        var next = output.Read(first.Cursor);
        Assert.Equal("🙂\u001b[31mRED\u001b[0m", next.Text);
        Assert.False(next.HasMore);
        Assert.Equal("", output.Read(next.Cursor).Text);
    }

    [Fact]
    public void BoundedOutputReportsHistoryGapAndRejectsFutureCursor()
    {
        var output = new TerminalOutputBuffer(8);
        output.Append(Encoding.UTF8.GetBytes("0123456789"));
        var page = output.Read(0);
        Assert.True(page.Gap);
        Assert.Equal(2, page.OldestCursor);
        Assert.Equal("23456789", page.Text);
        Assert.Equal(10, page.Cursor);
        Assert.Throws<ArgumentOutOfRangeException>(() => output.Read(11));
    }

    private static TerminalBridge Create(AutomationRemote remote) => new(remote, new AutomationDispatcher(), NullLogger.Instance);
}

internal sealed class AutomationDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => true;
    public void Post(Action action) => action();
}

internal sealed class AutomationRemote : IRemoteTerminal
{
    public int Columns => 120;
    public int Rows => 32;
    public bool IsOpen => true;
    public List<string> Writes { get; } = [];
    public Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? BeforeWrite { get; set; }
    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;
    public event EventHandler? Closed { add { } remove { } }
    public void Emit(string text) => OutputReceived?.Invoke(this, new TerminalOutputEventArgs(Encoding.UTF8.GetBytes(text)));
    public Task StartAsync(TerminalStartOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Writes.Add(Encoding.UTF8.GetString(data.Span));
        return BeforeWrite?.Invoke(data, cancellationToken) ?? ValueTask.CompletedTask;
    }
    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
