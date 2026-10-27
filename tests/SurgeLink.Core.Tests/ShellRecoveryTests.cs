using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.App.Services;
using SurgeLink.App.Sessions;
using SurgeLink.Core.Files;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Storage;
using SurgeLink.Core.Terminal;
using SurgeLink.Core.Tests.Fakes;

namespace SurgeLink.Core.Tests;

public sealed class ShellRecoveryTests
{
    [Fact]
    public async Task ShellClosureAfterDisconnectKeepsTheConnectionFailureMessage()
    {
        await using var harness = await Harness.OpenAsync();
        harness.Session.RaiseStateChanged(ConnectionState.Connected, ConnectionState.Faulted, "Network connection lost.");
        harness.Original.Exit();
        Assert.Equal(ConnectionState.Faulted, harness.Tab.State);
        Assert.Equal("Network connection lost.", harness.Tab.Message);
    }

    [Fact]
    public async Task ShellRecoveryKeepsTransportTabAndTransferQueue()
    {
        await using var harness = await Harness.OpenAsync();
        var tab = harness.Tab;
        var old = tab.Terminal;
        var queue = tab.Transfers;
        harness.Workspace.SetFileBrowserPath(tab.Id, "/data");
        harness.Original.Exit();
        Assert.True(old!.IsClosed);
        Assert.Equal(ConnectionState.Connected, tab.State);
        Assert.Contains("shell exited", tab.Message);
        var drained = false;
        harness.Workspace.TerminalReplacing += current =>
        {
            Assert.Same(old, current.Terminal);
            Assert.True(current.IsReopeningTerminal);
            drained = true;
            return Task.CompletedTask;
        };
        await harness.Workspace.ReopenTerminalAsync(tab.Id);
        Assert.True(drained);
        Assert.Same(tab, Assert.Single(harness.Workspace.Snapshot.Tabs));
        Assert.NotSame(old, tab.Terminal);
        Assert.False(tab.Terminal!.IsClosed);
        Assert.Same(queue, tab.Transfers);
        Assert.Equal("/data", tab.FileBrowserPath);
        Assert.Null(tab.Message);
        Assert.Equal(1, harness.Plugin.ConnectCalls);
        Assert.Equal(0, harness.Session.DisposeCount);
    }

    [Fact]
    public async Task ReopenWaitsForExternalAccessToDrainAndSuppressesDuplicates()
    {
        await using var harness = await Harness.OpenAsync();
        harness.Original.Exit();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Workspace.TerminalReplacing += _ => { entered.TrySetResult(); return release.Task; };
        var reopening = harness.Workspace.ReopenTerminalAsync(harness.Tab.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, harness.Replacements);
        await harness.Workspace.ReopenTerminalAsync(harness.Tab.Id);
        release.TrySetResult();
        await reopening.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, harness.Replacements);
    }

    [Fact]
    public async Task FailedDrainLeavesTheClosedShellRetryable()
    {
        await using var harness = await Harness.OpenAsync();
        var old = harness.Tab.Terminal;
        harness.Original.Exit();
        harness.Workspace.TerminalReplacing += _ => Task.FromException(new IOException("drain failed"));
        await Assert.ThrowsAsync<IOException>(() => harness.Workspace.ReopenTerminalAsync(harness.Tab.Id));
        Assert.False(harness.Tab.IsReopeningTerminal);
        Assert.Same(old, harness.Tab.Terminal);
        Assert.Equal(0, harness.Replacements);
        Assert.Equal(ConnectionState.Connected, harness.Tab.State);
    }

    [Fact]
    public async Task ClosingDuringReopenCannotResurrectTheTab()
    {
        await using var harness = await Harness.OpenAsync();
        harness.Original.Exit();
        var pending = new TaskCompletionSource<IRemoteTerminal>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Session.TerminalFactory = _ => pending.Task;
        var reopening = harness.Workspace.ReopenTerminalAsync(harness.Tab.Id);
        await harness.Workspace.CloseAsync(harness.Tab.Id);
        var incoming = new ControlledTerminal();
        pending.TrySetResult(incoming);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reopening);
        Assert.Empty(harness.Workspace.Snapshot.Tabs);
        Assert.Equal(1, incoming.DisposeCount);
        Assert.False(harness.Tab.IsReopeningTerminal);
    }

    [Fact]
    public async Task DisconnectRejectsNewTransfersAndShellRecovery()
    {
        await using var harness = await Harness.OpenAsync();
        harness.Session.RaiseStateChanged(ConnectionState.Connected, ConnectionState.Disconnected);
        var item = new TransferItem { Id = "offline", Name = "file", RemotePath = "/file", Direction = TransferDirection.Upload };
        Assert.Throws<InvalidOperationException>(() => harness.Tab.Transfers.Enqueue(item, new SurgeLink.Protocols.Demo.DemoFileSystem()));
        Assert.Equal(TransferStatus.Failed, item.Status);
        await harness.Workspace.ReopenTerminalAsync(harness.Tab.Id);
        Assert.Equal(0, harness.Replacements);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly SessionManager _manager;
        public FakeProtocolPlugin Plugin { get; } = new();
        public FakeProtocolSession Session { get; }
        public ControlledTerminal Original { get; } = new();
        public WorkspaceStore Workspace { get; }
        public WorkspaceTab Tab { get; private set; } = null!;
        public int Replacements { get; private set; }
        private Harness()
        {
            Session = new FakeProtocolSession(Plugin.Descriptor) { Terminal = Original };
            Session.TerminalFactory = _ => { Replacements++; return Task.FromResult<IRemoteTerminal>(new ControlledTerminal()); };
            Plugin.SessionFactory = _ => Session;
            var catalog = new ProtocolCatalog(); catalog.Register(Plugin);
            var credentials = new FakeCredentialStore();
            _manager = new SessionManager(catalog, credentials,
                new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
            Workspace = new WorkspaceStore(_manager, new JsonSessionStore(_temp.File("sessions.json")), credentials,
                new AutomationDispatcher(), NullLogger<WorkspaceStore>.Instance);
        }
        public static async Task<Harness> OpenAsync()
        {
            var harness = new Harness();
            harness.Tab = await harness.Workspace.OpenAsync(SessionProfile.New("fake", "host", 22, "user"), null, default);
            return harness;
        }
        public async ValueTask DisposeAsync()
        {
            await Workspace.CloseAsync(Tab.Id);
            await _manager.DisposeAsync();
            _temp.Dispose();
        }
    }

    private sealed class ControlledTerminal : IRemoteTerminal
    {
        public int Columns => 80;
        public int Rows => 24;
        public bool IsOpen { get; private set; }
        public int DisposeCount { get; private set; }
        public event EventHandler<TerminalOutputEventArgs>? OutputReceived { add { } remove { } }
        public event EventHandler? Closed;
        public void Exit() { IsOpen = false; Closed?.Invoke(this, EventArgs.Empty); }
        public Task StartAsync(TerminalStartOptions options, CancellationToken cancellationToken) { IsOpen = true; return Task.CompletedTask; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { DisposeCount++; IsOpen = false; return ValueTask.CompletedTask; }
    }
}
