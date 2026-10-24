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
        await service.SetAccessAsync(tab, AgentAccessMode.Control);
        Assert.Equal(AgentAccessMode.Control, service.GetMode(tab.Id));
        var client = new AgentClient(AgentEndpoint.ForProcess(Environment.ProcessId));
        var listed = await client.SendAsync(new("sessions.list"));
        var handle = Assert.Single(listed.Data!.Value.EnumerateArray()).GetProperty("handle").GetString()!;
        workspace.SetView(tab.Id, SessionViewKind.Files);
        var sent = await client.SendAsync(new("terminal.text", handle, Text: "echo hello", Submit: true, ExpectedInputRevision: 0, OperationId: "visible-command"));
        Assert.True(sent.Success);
        Assert.Equal(SessionViewKind.Terminal, tab.View);
        Assert.Equal("echo hello\r", Assert.Single(remote.Writes));
        remote.Emit("echo hello\r\nhello\r\n$ ");
        Assert.Contains("hello", (await client.SendAsync(new("terminal.screen", handle))).Data!.Value.GetProperty("text").GetString());
        tab.Terminal!.SendText("human");
        Assert.Equal(AgentAccessMode.None, service.GetMode(tab.Id));
        Assert.Equal("session_unavailable", (await client.SendAsync(new("terminal.key", handle, Key: "Enter", ExpectedInputRevision: 2, OperationId: "old-control"))).ErrorCode);
        Assert.Contains(service.Activity, a => a.Outcome == "human_input");
        Assert.Equal(new[] { "echo hello\r", "human" }, remote.Writes);
        await workspace.CloseAsync(tab.Id);
    }
}
