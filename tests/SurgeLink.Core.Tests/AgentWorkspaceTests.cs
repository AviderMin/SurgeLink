using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.Agent;
using SurgeLink.App.Services;
using SurgeLink.App.Sessions;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Storage;
using SurgeLink.Core.Tests.Fakes;

namespace SurgeLink.Core.Tests;

public sealed class AgentWorkspaceTests
{
    [Theory]
    [InlineData("sessions.activate")]
    [InlineData("terminal.text")]
    [InlineData("terminal.key")]
    public async Task HumanPauseBlocksActivationAlreadyQueuedOnTheUi(string method)
    {
        using var temp = new TempDirectory();
        var remote = new AutomationRemote();
        var catalog = new ProtocolCatalog();
        catalog.Register(new FakeProtocolPlugin { SessionFactory = _ => new FakeProtocolSession(new("fake", "Fake", 0, SessionCapabilities.Terminal)) { Terminal = remote } });
        var credentials = new FakeCredentialStore();
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, new JsonSessionStore(temp.File("sessions.json")), credentials,
            new AutomationDispatcher(), NullLogger<WorkspaceStore>.Instance);
        var tab = await workspace.OpenAsync(SessionProfile.New("fake", "host.test", 22, "alice"), null, default);
        var dispatcher = new DeferredDispatcher();
        var pipeName = $"SurgeLink.Tests.{Guid.NewGuid():N}";
        await using var service = new AgentWorkspaceService(workspace, dispatcher, NullLogger.Instance, pipeName);
        await service.SetAccessAsync(tab, AgentAccessMode.Control);
        var client = new AgentClient(pipeName);
        var listed = await client.SendAsync(new("sessions.list"));
        var handle = Assert.Single(listed.Data!.Value.EnumerateArray()).GetProperty("handle").GetString()!;
        workspace.SetView(tab.Id, SessionViewKind.Files);
        var check = await client.SendAsync(new("sessions.check", handle));
        dispatcher.Defer = true;
        var pending = client.SendAsync(new(method, handle, View: "terminal", Text: "agent", Key: "Enter",
            ExpectedInputRevision: 0, OperationId: "deferred",
            CheckToken: check.Data!.Value.GetProperty("checkToken").GetString()));
        try
        {
            await dispatcher.Posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            tab.Terminal!.SendText("human");
            dispatcher.RunNext();
            Assert.Equal("user_operating", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).ErrorCode);
            Assert.Equal(SessionViewKind.Files, tab.View);
            Assert.Equal(new[] { "human" }, remote.Writes);
            Assert.Equal(AgentAccessMode.Control, service.GetMode(tab.Id));
        }
        finally { dispatcher.Defer = false; dispatcher.Drain(); }
        await workspace.CloseAsync(tab.Id);
    }

    private sealed class DeferredDispatcher : IUiDispatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();
        public bool Defer { get; set; }
        public bool IsOnUiThread => true;
        public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Post(Action action)
        {
            if (!Defer) { action(); return; }
            _queue.Enqueue(action);
            Posted.TrySetResult();
        }
        public void RunNext() { Assert.True(_queue.TryDequeue(out var action)); action(); }
        public void Drain() { while (_queue.TryDequeue(out var action)) action(); }
    }

    [Fact]
    public async Task DesktopCanBeDiscoveredBeforeSharingAndPresenceDoesNotGrantAccess()
    {
        using var temp = new TempDirectory();
        var catalog = new ProtocolCatalog();
        var credentials = new FakeCredentialStore();
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, new JsonSessionStore(temp.File("sessions.json")), credentials,
            new AutomationDispatcher(), NullLogger<WorkspaceStore>.Instance);
        var pipeName = $"SurgeLink.Tests.{Guid.NewGuid():N}";
        await using var service = new AgentWorkspaceService(workspace, new AutomationDispatcher(), NullLogger.Instance, pipeName);
        var client = new AgentClient(pipeName);
        var result = await client.SendAsync(new("sessions.list"));
        Assert.True(result.Success);
        Assert.Empty(result.Data!.Value.EnumerateArray());
        Assert.False(service.HasAccess);
        using var config = System.Text.Json.JsonDocument.Parse(service.McpConfiguration);
        Assert.Empty(config.RootElement.GetProperty("mcpServers").GetProperty("surgelink").GetProperty("args").EnumerateArray());
        Assert.True((await client.SendAsync(new("client.heartbeat", ClientId: new string('B', 48), ClientName: "Test MCP"))).Success);
        Assert.Single(service.McpPeers);
        Assert.False(service.HasAccess);
        Assert.DoesNotContain(service.Activity, a => a.Operation == "client.heartbeat");
    }

    [Theory]
    [InlineData(AgentAccessMode.Observe, false)]
    [InlineData(AgentAccessMode.FileRead, true)]
    [InlineData(AgentAccessMode.Control, true)]
    public async Task DesktopGrantIncludesFileReadingOnlyWhenGranted(AgentAccessMode mode, bool canReadFiles)
    {
        using var temp = new TempDirectory();
        var catalog = new ProtocolCatalog(); catalog.Register(new SurgeLink.Protocols.Demo.DemoProtocolPlugin());
        var credentials = new FakeCredentialStore();
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, new JsonSessionStore(temp.File("sessions.json")), credentials,
            new AutomationDispatcher(), NullLogger<WorkspaceStore>.Instance);
        var tab = await workspace.OpenAsync(SessionProfile.New("demo", "demo", 0, "demo"), null, default);
        var pipeName = $"SurgeLink.Tests.{Guid.NewGuid():N}";
        await using var service = new AgentWorkspaceService(workspace, new AutomationDispatcher(), NullLogger.Instance, pipeName);
        Assert.Equal(AgentAccessMode.None, service.GetMode(tab.Id));
        await service.SetAccessAsync(tab, mode);
        var client = new AgentClient(pipeName);
        var sessions = await client.SendAsync(new("sessions.list"));
        var handle = Assert.Single(sessions.Data!.Value.EnumerateArray()).GetProperty("handle").GetString()!;
        if (canReadFiles)
        {
            Assert.True((await client.SendAsync(new("files.list", handle, "/home/demo"))).Success);
            var read = await client.SendAsync(new("files.read", handle, "/home/demo/demo.txt"));
            Assert.True(read.Success);
            Assert.Contains("Hello from", read.Data!.Value.GetProperty("text").GetString());
        }
        else
        {
            Assert.Equal("file_access_required", (await client.SendAsync(new("files.list", handle, "/home/demo"))).ErrorCode);
            Assert.Equal("file_access_required", (await client.SendAsync(new("files.read", handle, "/home/demo/demo.txt"))).ErrorCode);
        }
        await service.StopAllAsync();
        Assert.Equal("session_unavailable", (await client.SendAsync(new("files.list", handle, "/home/demo"))).ErrorCode);
        Assert.Equal("session_unavailable", (await client.SendAsync(new("files.read", handle, "/home/demo/demo.txt"))).ErrorCode);
        await workspace.CloseAsync(tab.Id);
    }

    [Fact]
    public async Task DowngradingAccessRevokesTheOldHandle()
    {
        using var temp = new TempDirectory();
        var catalog = new ProtocolCatalog(); catalog.Register(new SurgeLink.Protocols.Demo.DemoProtocolPlugin());
        var credentials = new FakeCredentialStore();
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, new JsonSessionStore(temp.File("sessions.json")), credentials,
            new AutomationDispatcher(), NullLogger<WorkspaceStore>.Instance);
        var tab = await workspace.OpenAsync(SessionProfile.New("demo", "demo", 0, "demo"), null, default);
        var pipeName = $"SurgeLink.Tests.{Guid.NewGuid():N}";
        await using var service = new AgentWorkspaceService(workspace, new AutomationDispatcher(), NullLogger.Instance, pipeName);
        var client = new AgentClient(pipeName);

        await service.SetAccessAsync(tab, AgentAccessMode.FileRead);
        var first = await client.SendAsync(new("sessions.list"));
        var oldHandle = Assert.Single(first.Data!.Value.EnumerateArray()).GetProperty("handle").GetString()!;
        Assert.Equal(AgentAccessMode.FileRead, service.GetMode(tab.Id));

        await service.SetAccessAsync(tab, AgentAccessMode.Observe);
        Assert.Equal(AgentAccessMode.Observe, service.GetMode(tab.Id));
        Assert.Equal("session_unavailable",
            (await client.SendAsync(new("files.list", oldHandle, "/home/demo"))).ErrorCode);
        var second = await client.SendAsync(new("sessions.list"));
        var newHandle = Assert.Single(second.Data!.Value.EnumerateArray()).GetProperty("handle").GetString()!;
        Assert.NotEqual(oldHandle, newHandle);
        await workspace.CloseAsync(tab.Id);
    }

    [Fact]
    public async Task StopAllInvalidatesAccessChangeWaitingForAnInFlightWrite()
    {
        using var temp = new TempDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new AutomationRemote { BeforeWrite = (_, _) => { entered.TrySetResult(); return new ValueTask(release.Task); } };
        var plugin = new FakeProtocolPlugin { SessionFactory = _ => new FakeProtocolSession(new("fake", "Fake", 0, SessionCapabilities.Terminal)) { Terminal = remote } };
        var catalog = new ProtocolCatalog(); catalog.Register(plugin);
        var credentials = new FakeCredentialStore();
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, new JsonSessionStore(temp.File("sessions.json")), credentials,
            new AutomationDispatcher(), NullLogger<WorkspaceStore>.Instance);
        var tab = await workspace.OpenAsync(SessionProfile.New("fake", "host.test", 22, "alice"), null, default);
        var pipeName = $"SurgeLink.Tests.{Guid.NewGuid():N}";
        await using var service = new AgentWorkspaceService(workspace, new AutomationDispatcher(), NullLogger.Instance, pipeName);
        await service.SetAccessAsync(tab, AgentAccessMode.Control);
        var client = new AgentClient(pipeName);
        var listed = await client.SendAsync(new("sessions.list"));
        var handle = Assert.Single(listed.Data!.Value.EnumerateArray()).GetProperty("handle").GetString()!;
        var check = await client.SendAsync(new("sessions.check", handle));
        var send = client.SendAsync(new("terminal.text", handle, Text: "pending", ExpectedInputRevision: 0, OperationId: "pending",
            CheckToken: check.Data!.Value.GetProperty("checkToken").GetString()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var changing = service.SetAccessAsync(tab, AgentAccessMode.Observe);
            var stop = service.StopAllAsync();
            Assert.False(changing.IsCompleted);
            Assert.False(stop.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(changing, stop).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(AgentAccessMode.None, service.GetMode(tab.Id));
            _ = await send;
        }
        finally { release.TrySetResult(); }
        await workspace.CloseAsync(tab.Id);
    }

    [Fact]
    public async Task ControlUsesExistingVisibleTerminalAndHumanTypingPausesHandle()
    {
        using var temp = new TempDirectory();
        var remote = new AutomationRemote();
        var plugin = new FakeProtocolPlugin { SessionFactory = _ => new FakeProtocolSession(new("fake", "Fake", 0, SessionCapabilities.Terminal)) { Terminal = remote } };
        var catalog = new ProtocolCatalog(); catalog.Register(plugin);
        var credentials = new FakeCredentialStore();
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, new JsonSessionStore(temp.File("sessions.json")), credentials,
            new AutomationDispatcher(), NullLogger<WorkspaceStore>.Instance);
        var tab = await workspace.OpenAsync(SessionProfile.New("fake", "host.test", 22, "alice"), null, default);
        var pipeName = $"SurgeLink.Tests.{Guid.NewGuid():N}";
        await using var service = new AgentWorkspaceService(workspace, new AutomationDispatcher(), NullLogger.Instance, pipeName);
        var initialAccessRevision = service.AccessRevision;
        await service.SetAccessAsync(tab, AgentAccessMode.Control);
        Assert.Equal(AgentAccessMode.Control, service.GetMode(tab.Id));
        Assert.True(service.AccessRevision > initialAccessRevision);
        var controlAccessRevision = service.AccessRevision;
        var client = new AgentClient(pipeName);
        var listed = await client.SendAsync(new("sessions.list"));
        var handle = Assert.Single(listed.Data!.Value.EnumerateArray()).GetProperty("handle").GetString()!;
        workspace.SetView(tab.Id, SessionViewKind.Files);
        var check = await client.SendAsync(new("sessions.check", handle));
        Assert.Equal("host.test", check.Data!.Value.GetProperty("session").GetProperty("target").GetProperty("server").GetString());
        Assert.Equal("alice", check.Data.Value.GetProperty("session").GetProperty("target").GetProperty("username").GetString());
        var sent = await client.SendAsync(new("terminal.text", handle, Text: "echo hello", Submit: true, ExpectedInputRevision: 0, OperationId: "visible-command",
            CheckToken: check.Data.Value.GetProperty("checkToken").GetString()));
        Assert.True(sent.Success);
        Assert.Equal(SessionViewKind.Terminal, tab.View);
        Assert.Equal("echo hello\r", Assert.Single(remote.Writes));
        remote.Emit("echo hello\r\nhello\r\n$ ");
        Assert.Contains("hello", (await client.SendAsync(new("terminal.screen", handle))).Data!.Value.GetProperty("text").GetString());
        Assert.Equal(controlAccessRevision, service.AccessRevision);
        tab.Terminal!.SendText("human");
        Assert.Equal(AgentAccessMode.Control, service.GetMode(tab.Id));
        Assert.True(service.IsShared(tab.Id));
        Assert.True(service.IsHumanOperating(tab.Id));
        Assert.True(service.AccessRevision > controlAccessRevision);
        Assert.Equal("user_operating", (await client.SendAsync(new("terminal.key", handle, Key: "Enter", ExpectedInputRevision: 2, OperationId: "old-control"))).ErrorCode);
        Assert.Equal("user_operating", (await client.SendAsync(new("sessions.check", handle))).ErrorCode);
        Assert.Contains(service.Activity, a => a.Operation == "access" && a.Outcome == "human_input");
        Assert.Equal(new[] { "echo hello\r", "human" }, remote.Writes);

        var pausedAccessRevision = service.AccessRevision;
        await service.SetAccessAsync(tab, AgentAccessMode.Control);
        Assert.False(service.IsHumanOperating(tab.Id));
        Assert.Equal(AgentAccessMode.Control, service.GetMode(tab.Id));
        Assert.True(service.AccessRevision > pausedAccessRevision);
        Assert.Contains(service.Activity, a => a.Operation == "access" && a.Outcome == "human_resumed");
        var resumed = await client.SendAsync(new("sessions.check", handle));
        Assert.True(resumed.Success);
        Assert.Equal(handle, resumed.Data!.Value.GetProperty("session").GetProperty("handle").GetString());
        await workspace.CloseAsync(tab.Id);
    }
}
