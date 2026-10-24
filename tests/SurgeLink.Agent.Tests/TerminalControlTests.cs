using SurgeLink.Core.Terminal;
using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class TerminalControlTests
{
    [Fact]
    public async Task ReadAccessCannotSendOrSwitchViews()
    {
        var terminal = new TestAutomation();
        var gateway = new AgentGateway();
        var handle = gateway.GrantSession("tab", "Test", new AgentGatewayTests.TestSession(), null, terminal);
        Assert.True((await gateway.ExecuteAsync(new("terminal.screen", handle), default)).Success);
        Assert.Equal("control_required", (await gateway.ExecuteAsync(Input(handle), default)).ErrorCode);
        Assert.Equal("control_required", (await gateway.ExecuteAsync(new("sessions.activate", handle), default)).ErrorCode);
        Assert.Equal(0, terminal.Writes);
    }

    [Fact]
    public async Task RetryingSameOperationWritesExactlyOnceAndChangedArgumentsAreRejected()
    {
        var terminal = new TestAutomation();
        var gateway = new AgentGateway();
        var selected = 0;
        var handle = GrantControl(gateway, terminal,
            (_, _) => { selected++; return Task.CompletedTask; });
        var input = await CheckedInputAsync(gateway, handle);
        var first = await gateway.ExecuteAsync(input, default);
        var retry = await gateway.ExecuteAsync(input, default);
        Assert.True(first.Success);
        Assert.Equal(first, retry);
        Assert.Equal(1, terminal.Writes);
        Assert.Equal(1, selected);
        Assert.False(first.Data!.Value.GetProperty("commandCompletionKnown").GetBoolean());
        Assert.Equal("operation_conflict", (await gateway.ExecuteAsync(input with { Text = "different" }, default)).ErrorCode);
        Assert.Equal("session_check_required", (await gateway.ExecuteAsync(input with { OperationId = "new-id" }, default)).ErrorCode);
        Assert.Equal(1, terminal.Writes);
    }

    [Fact]
    public async Task UncertainWriteIsRememberedAndNotRetried()
    {
        var terminal = new TestAutomation { FailWrite = true };
        var gateway = new AgentGateway();
        var handle = GrantControl(gateway, terminal);
        var input = await CheckedInputAsync(gateway, handle);
        Assert.Equal("input_outcome_unknown", (await gateway.ExecuteAsync(input, default)).ErrorCode);
        Assert.Equal("input_outcome_unknown", (await gateway.ExecuteAsync(input, default)).ErrorCode);
        Assert.Equal(1, terminal.Writes);
    }

    [Fact]
    public async Task InputMustHaveRevisionAndOperationId()
    {
        var terminal = new TestAutomation();
        var gateway = new AgentGateway();
        var handle = gateway.GrantSession("tab", "Test", new AgentGatewayTests.TestSession(), null, terminal, true);
        Assert.Equal("invalid_arguments", (await gateway.ExecuteAsync(Input(handle) with { OperationId = null }, default)).ErrorCode);
        Assert.Equal("invalid_arguments", (await gateway.ExecuteAsync(Input(handle) with { ExpectedInputRevision = null }, default)).ErrorCode);
        Assert.Equal(0, terminal.Writes);
    }

    internal static AgentRequest Input(string handle) => new("terminal.text", handle,
        Text: "echo test", Submit: true, ExpectedInputRevision: 0, OperationId: "command-1");

    internal static string GrantControl(AgentGateway gateway, TestAutomation terminal,
        Func<string, CancellationToken, Task>? activate = null, Func<bool>? isActive = null, string connectionKey = "tab")
    {
        var session = new AgentGatewayTests.TestSession();
        return gateway.GrantSession(connectionKey, "Test", session, null, terminal, true, activate, isActive,
            new AgentSessionTarget(session.SessionId, "host.test", 2222, "alice"));
    }

    internal static async Task<AgentRequest> CheckedInputAsync(AgentGateway gateway, string handle)
    {
        var check = await gateway.ExecuteAsync(new("sessions.check", handle), default);
        Assert.True(check.Success, check.Message);
        return Input(handle) with
        {
            CheckToken = check.Data!.Value.GetProperty("checkToken").GetString(),
            ExpectedInputRevision = check.Data.Value.GetProperty("screen").GetProperty("inputRevision").GetInt64(),
        };
    }

    internal sealed class TestAutomation : ITerminalAutomation
    {
        public int Writes { get; private set; }
        public bool FailWrite { get; init; }
        private long _revision;
        public TerminalScreenSnapshot ReadScreen() => new("test", 80, 24, 0, 0, true, false, false, false, _revision, 0, false, false);
        public TerminalOutputPage ReadOutput(long? cursor) => new("output", 6, 0, 6, false, false);
        public Task<TerminalInputReceipt> SendTextAsync(string text, bool submit, long expectedRevision, CancellationToken ct)
        {
            if (_revision != expectedRevision) throw new TerminalInputConflictException();
            Writes++;
            _revision++;
            if (FailWrite) throw new TerminalInputUnknownException(new IOException("broken pipe"));
            return Task.FromResult(new TerminalInputReceipt(_revision, 0, text.Length + (submit ? 1 : 0), submit));
        }
        public Task<TerminalInputReceipt> SendKeyAsync(string key, long expectedRevision, CancellationToken ct) => SendTextAsync(key, false, expectedRevision, ct);
    }
}
