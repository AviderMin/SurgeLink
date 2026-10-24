using System.Diagnostics;
using System.Text.Json;
using SurgeLink.Cli;
using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class CliProcessTests
{
    [Fact]
    public async Task ExecutableReturnsJsonFromDesktopPipeAndUsageErrorsHaveExitCodeTwo()
    {
        var gateway = new AgentGateway((_, _) => Task.FromResult(false));
        await using var server = new AgentPipeServer(AgentEndpoint.ForProcess(Environment.ProcessId), gateway);
        var success = await RunAsync("sessions", "list", "--instance", Environment.ProcessId.ToString(), "--json");
        Assert.Equal(0, success.ExitCode);
        using var output = JsonDocument.Parse(success.Output);
        Assert.True(output.RootElement.GetProperty("success").GetBoolean());
        Assert.Empty(output.RootElement.GetProperty("data").EnumerateArray());

        var invalid = await RunAsync("files", "read");
        Assert.Equal(2, invalid.ExitCode);
        using var error = JsonDocument.Parse(invalid.Output);
        Assert.Equal("usage", error.RootElement.GetProperty("errorCode").GetString());

        var terminal = new TerminalControlTests.TestAutomation();
        var handle = TerminalControlTests.GrantControl(gateway, terminal);
        var checkedSession = await RunAsync("sessions", "check", "--instance", Environment.ProcessId.ToString(), "--session", handle);
        Assert.Equal(0, checkedSession.ExitCode);
        using var check = JsonDocument.Parse(checkedSession.Output);
        var checkToken = check.RootElement.GetProperty("data").GetProperty("checkToken").GetString()!;
        var inputArgs = new[] { "terminal", "send", "--instance", Environment.ProcessId.ToString(), "--session", handle,
            "--text", "echo visible", "--submit", "--revision", "0", "--operation-id", "cli-write", "--check-token", checkToken };
        var sent = await RunAsync(inputArgs);
        Assert.Equal(0, sent.ExitCode);
        using var receipt = JsonDocument.Parse(sent.Output);
        Assert.Equal("input_sent", receipt.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(0, (await RunAsync(inputArgs)).ExitCode);
        Assert.Equal(1, terminal.Writes);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.Equal("", await errors);
        return (process.ExitCode, await output);
    }
}
