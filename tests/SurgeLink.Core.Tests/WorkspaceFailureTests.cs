using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.App.Services;
using SurgeLink.App.Sessions;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Storage;
using SurgeLink.Core.Terminal;
using SurgeLink.Core.Tests.Fakes;

namespace SurgeLink.Core.Tests;

public sealed class WorkspaceFailureTests
{
    [Fact]
    public async Task CancellationDuringUnresponsiveHostProbeReleasesSessionAndBusyState()
    {
        var plugin = new FakeProtocolPlugin();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new TaskCompletionSource<RemoteHostInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeProtocolSession(plugin.Descriptor)
        {
            HostInfoLoader = _ => { started.TrySetResult(); return probe.Task; },
        };
        plugin.SessionFactory = _ => session;
        var catalog = new ProtocolCatalog(); catalog.Register(plugin);
        var credentials = new FakeCredentialStore();
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, new FailingStore(), credentials, new InlineDispatcher(), NullLogger<WorkspaceStore>.Instance);
        using var cancellation = new CancellationTokenSource();
        var opening = workspace.OpenAsync(SessionProfile.New("fake", "example.test", 22, "alice"), null, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(workspace.Snapshot.IsConnecting);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(workspace.Snapshot.IsConnecting);
        Assert.Empty(workspace.Snapshot.Tabs);
        Assert.Empty(manager.Sessions);
        Assert.Equal(1, session.DisposeCount);
        probe.SetResult(session.HostInfo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInitializationReleasesConnectionAndTerminal(bool terminalFails)
    {
        var plugin = new FakeProtocolPlugin();
        var terminal = new FailingTerminal(terminalFails);
        var session = new FakeProtocolSession(plugin.Descriptor) { Terminal = terminal };
        plugin.SessionFactory = _ => session;
        var catalog = new ProtocolCatalog(); catalog.Register(plugin);
        var credentials = new FakeCredentialStore();
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var profile = SessionProfile.New("fake", "example.test", 22, "alice");
        var workspace = new WorkspaceStore(manager, new FailingStore(profile), credentials, new InlineDispatcher(), NullLogger<WorkspaceStore>.Instance);
        await Assert.ThrowsAsync<IOException>(() => workspace.OpenAsync(
            profile, null, CancellationToken.None));
        Assert.Empty(manager.Sessions);
        Assert.Empty(workspace.Snapshot.Tabs);
        Assert.False(workspace.Snapshot.IsConnecting);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(1, terminal.DisposeCount);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool IsOnUiThread => true;
        public void Post(Action action) => action();
    }

    private sealed class FailingStore(SessionProfile? profile = null) : ISessionStore
    {
        public IReadOnlyList<SessionProfile> Load() => profile is null ? [] : [profile];
        public void Upsert(SessionProfile profile) => throw new IOException("disk full");
        public bool Delete(string profileId) => false;
    }

    private sealed class FailingTerminal(bool fail) : IRemoteTerminal
    {
        public int Columns => 80;
        public int Rows => 24;
        public bool IsOpen => !fail;
        public int DisposeCount { get; private set; }
        public event EventHandler<TerminalOutputEventArgs>? OutputReceived { add { } remove { } }
        public event EventHandler? Closed { add { } remove { } }
        public Task StartAsync(TerminalStartOptions options, CancellationToken cancellationToken) =>
            fail ? Task.FromException(new IOException("shell unavailable")) : Task.CompletedTask;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }
}
