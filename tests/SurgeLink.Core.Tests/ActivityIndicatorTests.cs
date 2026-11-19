using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.App.Services;
using SurgeLink.App.Sessions;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Storage;
using SurgeLink.Core.Terminal;
using SurgeLink.Core.Tests.Fakes;

namespace SurgeLink.Core.Tests;

/// <summary>Covers the tab activity indicator fed by background terminal output.</summary>
public sealed class ActivityIndicatorTests
{
    [Fact]
    public async Task ActivityIsCoalescedPerDispatcherTurn()
    {
        var remote = new ScriptedTerminal();
        var dispatcher = new QueuedDispatcher();
        await using var bridge = new TerminalBridge(remote, dispatcher, NullLogger.Instance);
        var activity = 0;
        bridge.Activity += (_, _) => activity++;

        remote.Emit("alpha");
        remote.Emit("beta");
        remote.Emit("gamma");
        Assert.Equal(0, activity);

        dispatcher.RunAll();
        Assert.Equal(1, activity);

        remote.Emit("delta");
        dispatcher.RunAll();
        Assert.Equal(2, activity);
    }

    [Fact]
    public async Task ActivityIgnoresResizeAndSettingsChanges()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettingsStore(temp.File("settings.json"));
        var remote = new ScriptedTerminal();
        var dispatcher = new QueuedDispatcher();
        await using var bridge = new TerminalBridge(remote, dispatcher, NullLogger.Instance, settings: settings);
        var activity = 0;
        bridge.Activity += (_, _) => activity++;

        await bridge.ResizeAsync(100, 40);
        dispatcher.RunAll();
        settings.Update(s => s with { TerminalScrollbackLines = 2000 });
        dispatcher.RunAll();
        Assert.Equal(0, activity);

        // Positive control: the same listener still observes real output.
        remote.Emit("output");
        dispatcher.RunAll();
        Assert.Equal(1, activity);
    }

    [Fact]
    public async Task OutputOnBackgroundTabSetsActivityAndNotifiesOnce()
    {
        using var temp = new TempDirectory();
        var workspace = CreateWorkspace(temp, new FakeProtocolPlugin(), null, out var manager);
        await using (manager)
        {
            var first = await workspace.OpenAsync(SessionProfile.New("fake", "first.test", 22, "alice"), null, default);
            var second = await workspace.OpenAsync(SessionProfile.New("fake", "second.test", 22, "alice"), null, default);
            Assert.Same(second, workspace.Snapshot.ActiveTab);
            var terminal = TerminalOf(first);
            var changed = 0;
            workspace.Changed += (_, _) => changed++;

            terminal.Emit("background one");
            Assert.True(first.HasActivity);
            Assert.False(second.HasActivity);
            Assert.Equal(1, changed);

            terminal.Emit("background two");
            Assert.True(first.HasActivity);
            Assert.Equal(1, changed);

            await workspace.CloseAsync(second.Id);
            await workspace.CloseAsync(first.Id);
        }
    }

    [Fact]
    public async Task OutputOnActiveTabDoesNotSetActivity()
    {
        using var temp = new TempDirectory();
        var workspace = CreateWorkspace(temp, new FakeProtocolPlugin(), null, out var manager);
        await using (manager)
        {
            var tab = await workspace.OpenAsync(SessionProfile.New("fake", "active.test", 22, "alice"), null, default);
            Assert.Same(tab, workspace.Snapshot.ActiveTab);
            var changed = 0;
            workspace.Changed += (_, _) => changed++;

            TerminalOf(tab).Emit("active output");
            Assert.False(tab.HasActivity);
            Assert.Equal(0, changed);

            await workspace.CloseAsync(tab.Id);
        }
    }

    [Fact]
    public async Task ActivatingTabClearsActivity()
    {
        using var temp = new TempDirectory();
        var workspace = CreateWorkspace(temp, new FakeProtocolPlugin(), null, out var manager);
        await using (manager)
        {
            var first = await workspace.OpenAsync(SessionProfile.New("fake", "first.test", 22, "alice"), null, default);
            var second = await workspace.OpenAsync(SessionProfile.New("fake", "second.test", 22, "alice"), null, default);
            var terminal = TerminalOf(first);
            terminal.Emit("while background");
            Assert.True(first.HasActivity);

            workspace.Activate(first.Id);

            Assert.Same(first, workspace.Snapshot.ActiveTab);
            Assert.False(first.HasActivity);
            terminal.Emit("while active");
            Assert.False(first.HasActivity);

            await workspace.CloseAsync(second.Id);
            await workspace.CloseAsync(first.Id);
        }
    }

    [Fact]
    public async Task ResizeAndSettingsChangesDoNotSetActivity()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettingsStore(temp.File("settings.json"));
        var workspace = CreateWorkspace(temp, new FakeProtocolPlugin(), settings, out var manager);
        await using (manager)
        {
            var first = await workspace.OpenAsync(SessionProfile.New("fake", "first.test", 22, "alice"), null, default);
            var second = await workspace.OpenAsync(SessionProfile.New("fake", "second.test", 22, "alice"), null, default);
            Assert.Same(second, workspace.Snapshot.ActiveTab);

            await first.Terminal!.ResizeAsync(100, 40);
            settings.Update(s => s with { TerminalScrollbackLines = 1500 });
            Assert.False(first.HasActivity);

            // Positive control: the same tab still reports real output.
            TerminalOf(first).Emit("background output");
            Assert.True(first.HasActivity);

            await workspace.CloseAsync(second.Id);
            await workspace.CloseAsync(first.Id);
        }
    }

    [Fact]
    public async Task ConfiguredTerminalTypeIsForwardedToTheRemoteTerminal()
    {
        using var temp = new TempDirectory();
        var workspace = CreateWorkspace(temp, new FakeProtocolPlugin(), null, out var manager);
        await using (manager)
        {
            var profile = SessionProfile.New("fake", "term.test", 22, "alice") with
            {
                Options = new Dictionary<string, string> { ["terminalType"] = "screen" },
            };
            var tab = await workspace.OpenAsync(profile, null, default);

            Assert.Equal("screen", TerminalOf(tab).StartedOptions?.TerminalType);

            await workspace.CloseAsync(tab.Id);
        }
    }

    [Fact]
    public async Task FinalOutputQueuedBeforeShellCloseStillSetsActivity()
    {
        using var temp = new TempDirectory();
        var dispatcher = new QueuedDispatcher();
        var workspace = CreateWorkspace(temp, new FakeProtocolPlugin(), null, out var manager, dispatcher);
        await using (manager)
        {
            var first = await workspace.OpenAsync(SessionProfile.New("fake", "first.test", 22, "alice"), null, default);
            var second = await workspace.OpenAsync(SessionProfile.New("fake", "second.test", 22, "alice"), null, default);
            dispatcher.RunAll();
            var terminal = TerminalOf(first);

            // Both events are queued before the UI processes either one: the
            // activity callback must not be discarded just because EOF set the
            // terminal's closed flag first.
            terminal.Emit("final output");
            terminal.RaiseClosed();
            Assert.False(first.HasActivity);

            dispatcher.RunAll();
            Assert.True(first.HasActivity);
            Assert.Equal("Remote shell exited.", first.Message);

            await workspace.CloseAsync(second.Id);
            await workspace.CloseAsync(first.Id);
        }
    }

    [Fact]
    public async Task ReopenedTerminalReportsBackgroundActivity()
    {
        using var temp = new TempDirectory();
        var plugin = new FakeProtocolPlugin();
        var firstTerminal = new ScriptedTerminal();
        var replacementTerminal = new ScriptedTerminal();
        var firstSession = new FakeProtocolSession(plugin.Descriptor)
        {
            Terminal = firstTerminal,
            TerminalFactory = _ => Task.FromResult<IRemoteTerminal>(replacementTerminal),
        };
        plugin.SessionFactory = request => request.Host == "first.test"
            ? firstSession
            : new FakeProtocolSession(plugin.Descriptor) { Terminal = new ScriptedTerminal() };
        var workspace = CreateWorkspace(temp, plugin, null, out var manager);
        await using (manager)
        {
            var first = await workspace.OpenAsync(SessionProfile.New("fake", "first.test", 22, "alice"), null, default);
            var second = await workspace.OpenAsync(SessionProfile.New("fake", "second.test", 22, "alice"), null, default);
            Assert.Same(second, workspace.Snapshot.ActiveTab);

            firstTerminal.RaiseClosed();
            Assert.True(first.Terminal!.IsClosed);
            await workspace.ReopenTerminalAsync(first.Id);
            Assert.Same(replacementTerminal, ((FakeProtocolSession)first.Session).Terminal);
            Assert.False(first.HasActivity);

            replacementTerminal.Emit("after reopen");
            Assert.True(first.HasActivity);

            await workspace.CloseAsync(second.Id);
            await workspace.CloseAsync(first.Id);
        }
    }

    private static WorkspaceStore CreateWorkspace(TempDirectory temp, FakeProtocolPlugin plugin,
        IAppSettingsStore? settings, out SessionManager manager, IUiDispatcher? dispatcher = null)
    {
        plugin.SessionFactory ??= _ => new FakeProtocolSession(plugin.Descriptor)
        {
            Terminal = new ScriptedTerminal(),
        };
        var catalog = new ProtocolCatalog();
        catalog.Register(plugin);
        var credentials = new FakeCredentialStore();
        manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        return new WorkspaceStore(manager, new JsonSessionStore(temp.File("sessions.json")), credentials,
            dispatcher ?? new InlineDispatcher(), NullLogger<WorkspaceStore>.Instance, settings);
    }

    private static ScriptedTerminal TerminalOf(WorkspaceTab tab) =>
        Assert.IsType<ScriptedTerminal>(Assert.IsType<FakeProtocolSession>(tab.Session).Terminal);

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool IsOnUiThread => true;
        public void Post(Action action) => action();
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _pending = new();
        public bool IsOnUiThread => false;
        public void Post(Action action) => _pending.Enqueue(action);
        public void RunAll()
        {
            while (_pending.Count > 0) { _pending.Dequeue()(); }
        }
    }

    private sealed class ScriptedTerminal : IRemoteTerminal
    {
        public int Columns => 120;
        public int Rows => 32;
        public bool IsOpen { get; private set; } = true;
        public event EventHandler<TerminalOutputEventArgs>? OutputReceived;
        public event EventHandler? Closed;
        public void Emit(string text) => OutputReceived?.Invoke(this, new TerminalOutputEventArgs(Encoding.UTF8.GetBytes(text)));
        public void RaiseClosed()
        {
            IsOpen = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }

        public TerminalStartOptions? StartedOptions { get; private set; }
        public Task StartAsync(TerminalStartOptions options, CancellationToken cancellationToken)
        {
            StartedOptions = options;
            return Task.CompletedTask;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            return ValueTask.CompletedTask;
        }
    }
}
