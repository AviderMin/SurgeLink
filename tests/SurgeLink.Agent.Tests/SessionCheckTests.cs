using System.Text.Json;
using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class SessionCheckTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScreenChangeWithoutHumanInputInvalidatesCheck(bool duringActivation)
    {
        var gateway = new AgentGateway();
        var terminal = new TerminalControlTests.TestAutomation();
        var handle = TerminalControlTests.GrantControl(gateway, terminal, (_, _) =>
        {
            if (duringActivation) terminal.ScreenText = "nested ssh / different prompt";
            return Task.CompletedTask;
        });
        var input = await TerminalControlTests.CheckedInputAsync(gateway, handle);
        if (!duringActivation) terminal.ScreenText = "nested ssh / different prompt";
        Assert.Equal("session_check_required", (await gateway.ExecuteAsync(input, default)).ErrorCode);
        Assert.Equal(0, terminal.Writes);
    }
    [Theory]
    [InlineData("terminal.text")]
    [InlineData("terminal.key")]
    public async Task MissingCheckBlocksInputBeforeSelectingOrWriting(string method)
    {
        var gateway = new AgentGateway();
        var terminal = new TerminalControlTests.TestAutomation();
        var selections = 0;
        var handle = TerminalControlTests.GrantControl(gateway, terminal, (_, _) => { selections++; return Task.CompletedTask; });
        var input = TerminalControlTests.Input(handle) with { Method = method, Key = "Enter" };
        Assert.Equal("session_check_required", (await gateway.ExecuteAsync(input, default)).ErrorCode);
        Assert.Equal(0, selections);
        Assert.Equal(0, terminal.Writes);
    }

    [Fact]
    public async Task CheckShowsTargetStateAndScreenWithoutClaimingEffectiveShellIdentity()
    {
        var gateway = new AgentGateway();
        var terminal = new TerminalControlTests.TestAutomation();
        var session = new AgentGatewayTests.TestSession();
        var target = new AgentSessionTarget(session.SessionId, "production.example", 2222, "deploy");
        var handle = gateway.GrantSession("tab", "Production", session, null, terminal, true, isActive: () => true, target: target);
        var response = await gateway.ExecuteAsync(new("sessions.check", handle), default);
        Assert.True(response.Success);
        var data = response.Data!.Value;
        var actual = data.GetProperty("session").GetProperty("target").Deserialize<AgentSessionTarget>(AgentJson.Options);
        Assert.Equal(target, actual);
        Assert.Equal("Connected", data.GetProperty("connectionState").GetString());
        Assert.True(data.GetProperty("session").GetProperty("isActive").GetBoolean());
        Assert.Equal("configured_login", data.GetProperty("usernameSource").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("effectiveShellUser").ValueKind);
        Assert.Equal("test", data.GetProperty("screen").GetProperty("text").GetString());
        Assert.Equal(0, terminal.Writes);
    }

    [Fact]
    public async Task CheckCannotBeIssuedWithoutKnownConnectionIdentity()
    {
        var gateway = new AgentGateway();
        var terminal = new TerminalControlTests.TestAutomation();
        var handle = gateway.GrantSession("tab", "Test", new AgentGatewayTests.TestSession(), null, terminal, true);
        Assert.Equal("session_context_unavailable", (await gateway.ExecuteAsync(new("sessions.check", handle), default)).ErrorCode);
        Assert.Equal("session_check_required", (await gateway.ExecuteAsync(TerminalControlTests.Input(handle), default)).ErrorCode);
        Assert.Equal(0, terminal.Writes);
    }

    [Fact]
    public async Task TokensCannotCrossConnectionsAndEachNewInputNeedsANewCheck()
    {
        var gateway = new AgentGateway();
        var firstTerminal = new TerminalControlTests.TestAutomation();
        var secondTerminal = new TerminalControlTests.TestAutomation();
        var first = TerminalControlTests.GrantControl(gateway, firstTerminal, connectionKey: "first");
        var second = TerminalControlTests.GrantControl(gateway, secondTerminal, connectionKey: "second");
        var checkedFirst = await TerminalControlTests.CheckedInputAsync(gateway, first);
        _ = await TerminalControlTests.CheckedInputAsync(gateway, second);
        Assert.Equal("session_check_required", (await gateway.ExecuteAsync(checkedFirst with { Session = second }, default)).ErrorCode);
        Assert.True((await gateway.ExecuteAsync(checkedFirst, default)).Success);
        Assert.Equal("session_check_required", (await gateway.ExecuteAsync(checkedFirst with { OperationId = "next", ExpectedInputRevision = 1 }, default)).ErrorCode);
        var next = await TerminalControlTests.CheckedInputAsync(gateway, first);
        Assert.True((await gateway.ExecuteAsync(next with { OperationId = "next" }, default)).Success);
        Assert.Equal(2, firstTerminal.Writes);
        Assert.Equal(0, secondTerminal.Writes);
    }

    [Fact]
    public async Task NewerCheckReplacesPreviousCheck()
    {
        var gateway = new AgentGateway();
        var terminal = new TerminalControlTests.TestAutomation();
        var handle = TerminalControlTests.GrantControl(gateway, terminal);
        var old = await TerminalControlTests.CheckedInputAsync(gateway, handle);
        var current = await TerminalControlTests.CheckedInputAsync(gateway, handle);
        Assert.Equal("session_check_required", (await gateway.ExecuteAsync(old, default)).ErrorCode);
        Assert.True((await gateway.ExecuteAsync(current, default)).Success);
        Assert.Equal(1, terminal.Writes);
    }

    [Fact]
    public async Task ExpiredCheckCannotWriteButExactCompletedRetryStillReturnsReceipt()
    {
        var clock = new TestClock();
        var gateway = new AgentGateway(timeProvider: clock);
        var terminal = new TerminalControlTests.TestAutomation();
        var handle = TerminalControlTests.GrantControl(gateway, terminal);
        var stale = await TerminalControlTests.CheckedInputAsync(gateway, handle);
        clock.Now = clock.Now.AddMinutes(2);
        Assert.Equal("session_check_required", (await gateway.ExecuteAsync(stale, default)).ErrorCode);
        var fresh = await TerminalControlTests.CheckedInputAsync(gateway, handle);
        var receipt = await gateway.ExecuteAsync(fresh, default);
        Assert.True(receipt.Success);
        clock.Now = clock.Now.AddMinutes(3);
        Assert.Equal(receipt, await gateway.ExecuteAsync(fresh, default));
        Assert.Equal(1, terminal.Writes);
    }

    [Fact]
    public async Task ActiveStateAndInputChangesInvalidateChecks()
    {
        var gateway = new AgentGateway();
        var terminal = new TerminalControlTests.TestAutomation();
        var active = true;
        var handle = TerminalControlTests.GrantControl(gateway, terminal, isActive: () => active);
        var input = await TerminalControlTests.CheckedInputAsync(gateway, handle);
        active = false;
        Assert.Equal("session_check_required", (await gateway.ExecuteAsync(input, default)).ErrorCode);
        input = await TerminalControlTests.CheckedInputAsync(gateway, handle);
        await terminal.SendKeyAsync("human", 0, default);
        Assert.Equal("session_check_required", (await gateway.ExecuteAsync(input, default)).ErrorCode);
        Assert.Equal(1, terminal.Writes);
    }

    [Fact]
    public async Task RegrantCannotReuseOldConnectionCheck()
    {
        var gateway = new AgentGateway();
        var terminal = new TerminalControlTests.TestAutomation();
        var handle = TerminalControlTests.GrantControl(gateway, terminal);
        var old = await TerminalControlTests.CheckedInputAsync(gateway, handle);
        await gateway.RevokeAsync("tab");
        var replacement = TerminalControlTests.GrantControl(gateway, terminal);
        Assert.Equal("session_unavailable", (await gateway.ExecuteAsync(old, default)).ErrorCode);
        Assert.Equal("session_check_required", (await gateway.ExecuteAsync(old with { Session = replacement }, default)).ErrorCode);
        Assert.Equal(0, terminal.Writes);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task CheckIsRevalidatedAfterDesktopSelectionCompletes()
    {
        var clock = new TestClock();
        var gateway = new AgentGateway(timeProvider: clock);
        var terminal = new TerminalControlTests.TestAutomation();
        var handle = TerminalControlTests.GrantControl(gateway, terminal, (_, _) =>
        {
            clock.Now = clock.Now.AddMinutes(2);
            return Task.CompletedTask;
        });
        var input = await TerminalControlTests.CheckedInputAsync(gateway, handle);
        Assert.Equal("session_check_required", (await gateway.ExecuteAsync(input, default)).ErrorCode);
        Assert.Equal(0, terminal.Writes);
    }
}
