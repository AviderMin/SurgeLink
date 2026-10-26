using System.Security.Cryptography;
using SurgeLink.Core.Terminal;
using SurgeLink.Core.Files;
using SurgeLink.AgentHost;
using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class DesktopConnectionTests
{
    [Fact]
    public async Task EachCallDiscoversCurrentDesktopAndOldHandlesCannotCrossRestarts()
    {
        var firstPid = RandomNumberGenerator.GetInt32(1000000, int.MaxValue);
        var secondPid = firstPid - 1;
        var firstGateway = new AgentGateway();
        var oldHandle = firstGateway.GrantSession("old", "Before restart", new AgentGatewayTests.TestSession(), () => "old");
        var desktop = firstPid;
        var resolves = 0;
        var connection = new DesktopConnection(resolveDesktop: () => { resolves++; return desktop; });
        await using (var server = new AgentPipeServer(AgentEndpoint.ForProcess(firstPid), firstGateway))
        {
            var listed = await connection.SendAsync(new("sessions.list"), default);
            Assert.Equal(oldHandle, Assert.Single(listed.Data!.Value.EnumerateArray()).GetProperty("handle").GetString());
        }

        desktop = secondPid;
        await using var restarted = new AgentPipeServer(AgentEndpoint.ForProcess(secondPid), new AgentGateway());
        Assert.Empty((await connection.SendAsync(new("sessions.list"), default)).Data!.Value.EnumerateArray());
        Assert.Equal("session_unavailable", (await connection.SendAsync(new("terminal.screen", oldHandle), default)).ErrorCode);
        Assert.Equal(3, resolves);
    }

    [Fact]
    public async Task AmbiguousDiscoveryDoesNotContactAnArbitraryDesktop()
    {
        var connection = new DesktopConnection(resolveDesktop: () => AgentEndpoint.SelectDesktopProcess([10, 20]));
        Assert.Equal("desktop_ambiguous", (await connection.SendAsync(new("sessions.list"), default)).ErrorCode);
        Assert.Throws<AgentDesktopDiscoveryException>(() => AgentEndpoint.SelectDesktopProcess([]));
        Assert.Equal(25, AgentEndpoint.SelectDesktopProcess([25]));
        var missing = new DesktopConnection(resolveDesktop: () => AgentEndpoint.SelectDesktopProcess([]));
        Assert.Equal("desktop_not_running", (await missing.SendAsync(new("sessions.list"), default)).ErrorCode);
    }

    [Fact]
    public async Task SlowReadDoesNotBlockListingOrAnotherSessionInput()
    {
        var pid = RandomNumberGenerator.GetInt32(1000000, int.MaxValue);
        var approvalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishApproval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new AgentGateway(async (_, ct) => { approvalStarted.SetResult(); using var registration = ct.Register(() => finishApproval.TrySetCanceled(ct)); return await finishApproval.Task; });
        var fileSession = new AgentGatewayTests.TestSession { FileSystem = new TestFiles() };
        var fileHandle = gateway.GrantSession("files", "Files", fileSession, null);
        var terminalHandle = TerminalControlTests.GrantControl(gateway, new TerminalControlTests.TestAutomation(), connectionKey: "terminal");
        await using var server = new AgentPipeServer(AgentEndpoint.ForProcess(pid), gateway);
        var connection = new DesktopConnection(pid);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = connection.SendAsync(new("files.read", fileHandle, "/slow"), timeout.Token);
        await approvalStarted.Task.WaitAsync(timeout.Token);

        Assert.True((await connection.SendAsync(new("sessions.list"), timeout.Token).WaitAsync(timeout.Token)).Success);
        var check = await connection.SendAsync(new("sessions.check", terminalHandle), timeout.Token);
        Assert.True(check.Success, check.Data?.GetRawText() ?? check.Message);
        var screen = check.Data!.Value.GetProperty("screen");
        var input = new AgentRequest("terminal.text", terminalHandle, Text: "echo test", ExpectedInputRevision: screen.GetProperty("inputRevision").GetInt64(),
            CheckToken: check.Data.Value.GetProperty("checkToken").GetString(), OperationId: "read-is-independent");
        Assert.True((await connection.SendAsync(input, timeout.Token).WaitAsync(timeout.Token)).Success);
        Assert.False(pending.IsCompleted);
        finishApproval.SetResult(true);
        Assert.True((await pending.WaitAsync(timeout.Token)).Success);
    }

    [Fact]
    public async Task SlowReadDoesNotBlockPresenceHeartbeat()
    {
        var pid = RandomNumberGenerator.GetInt32(1000000, int.MaxValue);
        var approvalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new AgentGateway(async (_, ct) => { approvalStarted.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return true; });
        var session = new AgentGatewayTests.TestSession { FileSystem = new TestFiles() };
        var handle = gateway.GrantSession("files", "Files", session, null);
        await using var server = new AgentPipeServer(AgentEndpoint.ForProcess(pid), gateway);
        var connection = new DesktopConnection(pid, discoverDesktops: () => [new(pid, null)]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = connection.SendAsync(new("files.read", handle, "/slow"), CancellationToken.None);
        await approvalStarted.Task.WaitAsync(timeout.Token);
        using var shutdown = new CancellationTokenSource();
        var presence = connection.RunPresenceAsync(shutdown.Token);
        connection.ClientConnected("Concurrent test");
        try
        {
            while (gateway.McpPeers.Count == 0) await Task.Delay(10, timeout.Token);
            Assert.Equal("Concurrent test", Assert.Single(gateway.McpPeers).Name);
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            await gateway.RevokeAsync("files").WaitAsync(timeout.Token);
            await shutdown.CancelAsync();
            await presence.WaitAsync(timeout.Token);
        }
        Assert.Equal("cancelled", (await pending.WaitAsync(timeout.Token)).ErrorCode);
    }

    [Fact]
    public async Task DesktopSelectionWaitsForEarlierInputAndOrdersLaterInput()
    {
        var firstPid = RandomNumberGenerator.GetInt32(1000000, int.MaxValue);
        var secondPid = firstPid - 1;
        var terminal = new BlockingAutomation();
        var firstGateway = new AgentGateway();
        var session = new AgentGatewayTests.TestSession();
        var handle = firstGateway.GrantSession("tab", "Demo", session, null, terminal, true,
            target: new AgentSessionTarget(session.SessionId, "host.test", 22, "user"));
        await using var first = new AgentPipeServer(AgentEndpoint.ForProcess(firstPid), firstGateway);
        await using var second = new AgentPipeServer(AgentEndpoint.ForProcess(secondPid), new AgentGateway());
        var connection = new DesktopConnection(firstPid,
            discoverDesktops: () => [new(firstPid, null), new(secondPid, null)]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var check = await connection.SendAsync(new("sessions.check", handle), timeout.Token);
        var input = new AgentRequest("terminal.text", handle, Text: "old", ExpectedInputRevision: 0,
            CheckToken: check.Data!.Value.GetProperty("checkToken").GetString(), OperationId: "old-input");
        var pendingInput = connection.SendAsync(input, timeout.Token);
        await terminal.Started.Task.WaitAsync(timeout.Token);
        var selection = connection.SendAsync(new("desktops.select", DesktopProcessId: secondPid), timeout.Token);
        await Task.Delay(50, timeout.Token);
        Assert.False(selection.IsCompleted);
        var newInput = connection.SendAsync(new("terminal.text", handle, Text: "new", ExpectedInputRevision: 0, OperationId: "new-input"), timeout.Token);

        terminal.Finish.SetResult(true);
        Assert.True((await pendingInput.WaitAsync(timeout.Token)).Success);
        Assert.True((await selection.WaitAsync(timeout.Token)).Success);
        Assert.Equal("session_unavailable", (await newInput.WaitAsync(timeout.Token)).ErrorCode);
        var selected = await connection.SendAsync(new("desktops.list"), timeout.Token);
        Assert.Equal(secondPid, selected.Data!.Value.GetProperty("selectedProcessId").GetInt32());
    }

    [Fact]
    public async Task BoundDesktopWithReusedPidRequiresExplicitSelectionBeforeCalls()
    {
        var pid = RandomNumberGenerator.GetInt32(1000000, int.MaxValue);
        var current = new AgentDesktopProcess(pid, null, DateTimeOffset.UtcNow.AddMinutes(-5));
        var connection = new DesktopConnection(pid, discoverDesktops: () => [current]);
        await using var endpoint = new AgentPipeServer(AgentEndpoint.ForProcess(pid), new AgentGateway());
        Assert.True((await connection.SendAsync(new("desktops.select", DesktopProcessId: pid), default)).Success);

        // Windows does not permit this test process to declare the same FirstPipeInstance
        // endpoint twice. Change the discovered process identity to model a replacement
        // process, independently of the pipe transport used by this test.
        current = new AgentDesktopProcess(pid, null, DateTimeOffset.UtcNow);
        var expired = await connection.SendAsync(new("sessions.list"), default);
        Assert.Equal("desktop_instance_expired", expired.ErrorCode);
        Assert.True((await connection.SendAsync(new("desktops.select", DesktopProcessId: pid), default)).Success);
        Assert.Empty((await connection.SendAsync(new("sessions.list"), default)).Data!.Value.EnumerateArray());
    }

    [Fact]
    public async Task BoundDesktopCanRecoverAfterRestartWithoutChangingClientConfiguration()
    {
        var oldPid = RandomNumberGenerator.GetInt32(1000000, int.MaxValue);
        var newPid = oldPid - 1;
        var connection = new DesktopConnection(oldPid, discoverDesktops: () => [new(newPid, null)]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var expired = await connection.SendAsync(new("sessions.list"), timeout.Token);
        Assert.Equal("desktop_instance_expired", expired.ErrorCode);
        await using var restarted = new AgentPipeServer(AgentEndpoint.ForProcess(newPid), new AgentGateway());
        var reset = await connection.SendAsync(new("desktops.select"), timeout.Token);
        Assert.True(reset.Success, reset.Data?.GetRawText() ?? reset.Message);
        Assert.Empty((await connection.SendAsync(new("sessions.list"), timeout.Token)).Data!.Value.EnumerateArray());
        Assert.True((await connection.SendAsync(new("desktops.select", DesktopProcessId: newPid), timeout.Token)).Success);

        Assert.Empty((await connection.SendAsync(new("sessions.list"), timeout.Token)).Data!.Value.EnumerateArray());
    }

    [Fact]
    public async Task PresenceStartsOnlyAfterClientDiscoveryAndIsRemovedOnShutdown()
    {
        var instance = RandomNumberGenerator.GetInt32(1000000, int.MaxValue);
        var gateway = new AgentGateway();
        await using var server = new AgentPipeServer(AgentEndpoint.ForProcess(instance), gateway);
        var connection = new DesktopConnection(instance);
        using var shutdown = new CancellationTokenSource();
        var presence = connection.RunPresenceAsync(shutdown.Token);
        Assert.Empty(gateway.McpPeers);
        connection.ClientConnected("Test MCP");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (gateway.McpPeers.Count == 0) await Task.Delay(10, timeout.Token);
            Assert.Equal("Test MCP", Assert.Single(gateway.McpPeers).Name);
        }
        finally { await shutdown.CancelAsync(); await presence; }
        Assert.Empty(gateway.McpPeers);
    }
    private sealed class TestFiles : IRemoteFileSystem
    {
        public bool SupportsPermissions => false;
        public Task<string> GetHomeDirectoryAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetCurrentDirectoryAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteFileEntry>>([]);
        public Task<RemoteFileEntry> StatAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream([1]));
        public Task<Stream> OpenWriteAsync(string path, long? size, IProgress<TransferProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string path, bool recursive, CancellationToken ct) => throw new NotSupportedException();
        public Task RenameAsync(string source, string destination, CancellationToken ct) => throw new NotSupportedException();
        public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class BlockingAutomation : ITerminalAutomation
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _revision;
        public TerminalScreenSnapshot ReadScreen() => new("test", 80, 24, 0, 0, true, false, false, false, _revision, 0, false, false);
        public TerminalOutputPage ReadOutput(long? cursor) => new("", 0, 0, 0, false, false);
        public async Task<TerminalInputReceipt> SendTextAsync(string text, bool submit, long expectedRevision, CancellationToken ct)
        {
            Started.TrySetResult();
            await Finish.Task.WaitAsync(ct);
            _revision++;
            return new(_revision, 0, text.Length, submit);
        }
        public Task<TerminalInputReceipt> SendKeyAsync(string key, long expectedRevision, CancellationToken ct) => SendTextAsync(key, false, expectedRevision, ct);
    }
}
