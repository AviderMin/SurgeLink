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
        await using var service = new AgentWorkspaceService(workspace, new AutomationDispatcher(), NullLogger.Instance);
        var client = new AgentClient(AgentEndpoint.ForProcess(Environment.ProcessId));
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
    [InlineData(AgentAccessMode.Observe)]
    [InlineData(AgentAccessMode.Control)]
    public async Task DesktopGrantExplicitlyIncludesFileReadingUntilRevoked(AgentAccessMode mode)
    {
        using var temp = new TempDirectory();
        var catalog = new ProtocolCatalog(); catalog.Register(new SurgeLink.Protocols.Demo.DemoProtocolPlugin());
        var credentials = new FakeCredentialStore();
        await using var manager = new SessionManager(catalog, credentials,
            new ProtocolHostContext(NullLoggerFactory.Instance, new FakeHostKeyStore()), NullLogger<SessionManager>.Instance);
        var workspace = new WorkspaceStore(manager, new JsonSessionStore(temp.File("sessions.json")), credentials,
            new AutomationDispatcher(), NullLogger<WorkspaceStore>.Instance);
        var tab = await workspace.OpenAsync(SessionProfile.New("demo", "demo", 0, "demo"), null, default);
        await using var service = new AgentWorkspaceService(workspace, new AutomationDispatcher(), NullLogger.Instance);
        Assert.Equal(AgentAccessMode.None, service.GetMode(tab.Id));
        await service.SetAccessAsync(tab, mode);
        var client = new AgentClient(AgentEndpoint.ForProcess(Environment.ProcessId));
        var sessions = await client.SendAsync(new("sessions.list"));
        var handle = Assert.Single(sessions.Data!.Value.EnumerateArray()).GetProperty("handle").GetString()!;
        Assert.True((await client.SendAsync(new("files.list", handle, "/home/demo"))).Success);
        var read = await client.SendAsync(new("files.read", handle, "/home/demo/demo.txt"));
        Assert.True(read.Success);
        Assert.Contains("Hello from", read.Data!.Value.GetProperty("text").GetString());
        await service.StopAllAsync();
        Assert.Equal("session_unavailable", (await client.SendAsync(new("files.list", handle, "/home/demo"))).ErrorCode);
        Assert.Equal("session_unavailable", (await client.SendAsync(new("files.read", handle, "/home/demo/demo.txt"))).ErrorCode);
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
        await using var service = new AgentWorkspaceService(workspace, new AutomationDispatcher(), NullLogger.Instance);
        await service.SetAccessAsync(tab, AgentAccessMode.Control);
        var client = new AgentClient(AgentEndpoint.ForProcess(Environment.ProcessId));
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
    public async Task ControlUsesExistingVisibleTerminalAndHumanTypingRevokesHandle()
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
        await using var service = new AgentWorkspaceService(workspace, new AutomationDispatcher(), NullLogger.Instance);
        var initialAccessRevision = service.AccessRevision;
        await service.SetAccessAsync(tab, AgentAccessMode.Control);
        Assert.Equal(AgentAccessMode.Control, service.GetMode(tab.Id));
        Assert.True(service.AccessRevision > initialAccessRevision);
        var controlAccessRevision = service.AccessRevision;
        var client = new AgentClient(AgentEndpoint.ForProcess(Environment.ProcessId));
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
        Assert.Equal(AgentAccessMode.None, service.GetMode(tab.Id));
        Assert.True(service.AccessRevision > controlAccessRevision);
        Assert.Equal("session_unavailable", (await client.SendAsync(new("terminal.key", handle, Key: "Enter", ExpectedInputRevision: 2, OperationId: "old-control"))).ErrorCode);
        Assert.Contains(service.Activity, a => a.Outcome == "human_input");
        Assert.Equal(new[] { "echo hello\r", "human" }, remote.Writes);
        await workspace.CloseAsync(tab.Id);
    }
}
