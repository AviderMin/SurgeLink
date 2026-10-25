using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.App.Services;
using SurgeLink.App.Sessions;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Storage;
using SurgeLink.Core.Tests.Fakes;

namespace SurgeLink.Core.Tests;

public sealed class WorkspaceRegressionTests
{
    [Fact]
    public async Task OpeningAndReconnectingUseTheSavedHistoryLimit()
    {
        using var temp = new TempDirectory();
        var catalog = new ProtocolCatalog(); catalog.Register(new SurgeLink.Protocols.Demo.DemoProtocolPlugin());
        var credentials = new FakeCredentialStore();
        var settings = new AppSettingsStore(temp.File("settings.json"));
        settings.Update(s => s with { TerminalScrollbackLines = 5000 });
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, new JsonSessionStore(temp.File("sessions.json")), credentials,
            new InlineDispatcher(), NullLogger<WorkspaceStore>.Instance, settings);
        var tab = await workspace.OpenAsync(SessionProfile.New("demo", "demo", 0, "demo"), null, default);
        Assert.Equal(5000, tab.Terminal!.Emulator.ScrollbackLimit);
        settings.Update(s => s with { TerminalScrollbackLines = 1000 });
        Assert.Equal(1000, tab.Terminal.Emulator.ScrollbackLimit);
        await workspace.ReconnectAsync(tab.Id);
        var reconnected = Assert.Single(workspace.Snapshot.Tabs);
        Assert.Equal(1000, reconnected.Terminal!.Emulator.ScrollbackLimit);
        await workspace.CloseAsync(reconnected.Id);
    }

    [Fact]
    public async Task FailedPrecloseDrainLeavesConnectionOpenAndRetryable()
    {
        using var temp = new TempDirectory();
        var catalog = new ProtocolCatalog(); catalog.Register(new FakeProtocolPlugin());
        var credentials = new FakeCredentialStore();
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, new JsonSessionStore(temp.File("sessions.json")), credentials,
            new InlineDispatcher(), NullLogger<WorkspaceStore>.Instance);
        var tab = await workspace.OpenAsync(SessionProfile.New("fake", "host.test", 22, "alice"), null, default);
        var attempts = 0;
        workspace.SessionClosing += _ => ++attempts == 1 ? Task.FromException(new IOException("drain failed")) : Task.CompletedTask;
        await Assert.ThrowsAsync<IOException>(() => workspace.CloseAsync(tab.Id));
        Assert.False(tab.IsClosing);
        Assert.Equal(0, ((FakeProtocolSession)tab.Session).DisposeCount);
        Assert.Same(tab, Assert.Single(workspace.Snapshot.Tabs));
        await workspace.CloseAsync(tab.Id);
        Assert.Equal(1, ((FakeProtocolSession)tab.Session).DisposeCount);
        Assert.Empty(workspace.Snapshot.Tabs);
    }

    [Fact]
    public async Task ClosingRevokesAndDrainsExternalAccessBeforeDisposingConnection()
    {
        using var temp = new TempDirectory();
        var plugin = new FakeProtocolPlugin();
        var catalog = new ProtocolCatalog(); catalog.Register(plugin);
        var credentials = new FakeCredentialStore();
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, new JsonSessionStore(temp.File("sessions.json")), credentials,
            new InlineDispatcher(), NullLogger<WorkspaceStore>.Instance);
        var tab = await workspace.OpenAsync(SessionProfile.New("fake", "host.test", 22, "alice"), null, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.SessionClosing += closing =>
        {
            Assert.Same(tab, closing);
            Assert.True(closing.IsClosing);
            entered.SetResult();
            return drain.Task;
        };
        var close = workspace.CloseAsync(tab.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, ((FakeProtocolSession)tab.Session).DisposeCount);
        Assert.False(close.IsCompleted);
        Assert.Same(close, workspace.CloseAsync(tab.Id));
        drain.SetResult();
        await close.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, ((FakeProtocolSession)tab.Session).DisposeCount);
        Assert.Empty(workspace.Snapshot.Tabs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ClosingMiddleTabKeepsSurvivingSessionIdentityAndView(int activeIndex)
    {
        using var temp = new TempDirectory();
        var plugin = new FakeProtocolPlugin();
        var catalog = new ProtocolCatalog(); catalog.Register(plugin);
        var credentials = new FakeCredentialStore();
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, new JsonSessionStore(temp.File("sessions.json")), credentials,
            new InlineDispatcher(), NullLogger<WorkspaceStore>.Instance);
        var tabs = new WorkspaceTab[3];
        for (var index = 0; index < tabs.Length; index++)
        {
            // Equal titles must not make distinct session instances interchangeable.
            tabs[index] = await workspace.OpenAsync(
                SessionProfile.New("fake", $"host-{index}.test", 22, "alice", "Same title"),
                null, CancellationToken.None);
        }
        workspace.SetView(tabs[2].Id, SessionViewKind.Files);
        workspace.SetFileBrowserPath(tabs[2].Id, "/third-session");
        workspace.Activate(tabs[activeIndex].Id);

        await workspace.CloseAsync(tabs[1].Id);

        Assert.Collection(workspace.Snapshot.Tabs,
            first => Assert.Same(tabs[0], first),
            third => Assert.Same(tabs[2], third));
        var expectedActive = activeIndex == 0 ? tabs[0] : tabs[2];
        Assert.Same(expectedActive, workspace.Snapshot.ActiveTab);
        Assert.Equal(SessionViewKind.Files, tabs[2].View);
        Assert.Equal("/third-session", tabs[2].FileBrowserPath);
        Assert.DoesNotContain(manager.Sessions, session => session.SessionId == tabs[1].Session.SessionId);
        workspace.Activate(tabs[2].Id);
        Assert.Same(tabs[2], workspace.Snapshot.ActiveTab);
        await workspace.CloseAsync(tabs[0].Id);
        await workspace.CloseAsync(tabs[2].Id);
        Assert.Empty(workspace.Snapshot.Tabs);
        Assert.Null(workspace.Snapshot.ActiveTab);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task LateConnectionCompletionDoesNotUndoEditsOrDeletion(bool delete, bool renameOnly)
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(temp.File("sessions.json"));
        var plugin = new FakeProtocolPlugin();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new TaskCompletionSource<RemoteHostInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        plugin.SessionFactory = _ => new FakeProtocolSession(plugin.Descriptor)
        {
            HostInfoLoader = _ => { started.SetResult(); return probe.Task; },
        };
        var catalog = new ProtocolCatalog(); catalog.Register(plugin);
        var credentials = new FakeCredentialStore();
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, store, credentials, new InlineDispatcher(), NullLogger<WorkspaceStore>.Instance);
        var profile = SessionProfile.New("fake", "old.test", 22, "alice");
        workspace.SaveProfile(profile);
        var opening = workspace.OpenAsync(profile, null, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var edited = profile with { Host = renameOnly ? profile.Host : "new.test", Name = "New name" };
        if (delete) { workspace.DeleteProfile(profile.Id); }
        else { workspace.SaveProfile(edited); }
        probe.SetResult(RemoteHostInfo.Empty);
        var tab = await opening;
        if (delete) { Assert.Empty(store.Load()); }
        else
        {
            var saved = Assert.Single(store.Load());
            Assert.Equal(edited.Host, saved.Host);
            Assert.Equal(edited.Name, saved.Name);
            if (renameOnly) { Assert.NotNull(saved.LastUsedUtc); }
            else { Assert.Null(saved.LastUsedUtc); }
        }
        await workspace.CloseAsync(tab.Id);
    }

    [Fact]
    public async Task ReconnectUsesCurrentProfileAndOneTimeSecretWithoutResurrectingDeletedProfile()
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(temp.File("sessions.json"));
        var plugin = new FakeProtocolPlugin();
        var catalog = new ProtocolCatalog(); catalog.Register(plugin);
        var credentials = new FakeCredentialStore();
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, store, credentials, new InlineDispatcher(), NullLogger<WorkspaceStore>.Instance);
        var profile = SessionProfile.New("fake", "old.test", 22, "alice");
        workspace.SaveProfile(profile);
        var tab = await workspace.OpenAsync(profile, new ConnectOptions { Password = "first" }, CancellationToken.None);
        workspace.SaveProfile(profile with { Host = "new.test", Name = "Renamed" });
        await workspace.ReconnectAsync(tab.Id, new ConnectOptions { Password = "second" });
        Assert.Equal("new.test", plugin.LastRequest!.Host);
        Assert.Equal("second", plugin.LastRequest.Password);
        Assert.Equal("Renamed", Assert.Single(store.Load()).Name);
        Assert.Null(credentials.Get(profile.Id));
        tab = Assert.Single(workspace.Snapshot.Tabs);
        workspace.DeleteProfile(profile.Id);
        await workspace.ReconnectAsync(tab.Id, new ConnectOptions { Password = "third" });
        Assert.Empty(store.Load());
        await workspace.CloseAsync(Assert.Single(workspace.Snapshot.Tabs).Id);
    }

    [Fact]
    public async Task CancelledReconnectKeepsOriginalTab()
    {
        using var temp = new TempDirectory();
        var plugin = new FakeProtocolPlugin();
        var catalog = new ProtocolCatalog(); catalog.Register(plugin);
        var credentials = new FakeCredentialStore();
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, new JsonSessionStore(temp.File("sessions.json")), credentials,
            new InlineDispatcher(), NullLogger<WorkspaceStore>.Instance);
        var tab = await workspace.OpenAsync(SessionProfile.New("fake", "host", 22, "alice"), null, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.ReconnectAsync(tab.Id,
            cancellationToken: new CancellationToken(true)));
        Assert.Same(tab, Assert.Single(workspace.Snapshot.Tabs));
        await workspace.CloseAsync(tab.Id);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool IsOnUiThread => true;
        public void Post(Action action) => action();
    }
}
