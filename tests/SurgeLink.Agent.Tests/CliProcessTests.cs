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

        var startInvalidEnv = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        startInvalidEnv.Environment["SURGELINK_INSTANCE"] = "not-a-pid";
        startInvalidEnv.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        startInvalidEnv.ArgumentList.Add("sessions");
        startInvalidEnv.ArgumentList.Add("list");
        var badEnv = await RunProcessAsync(startInvalidEnv);
        Assert.Equal(2, badEnv.ExitCode);
        using var badEnvJson = JsonDocument.Parse(badEnv.Output);
        Assert.Equal("usage", badEnvJson.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task InvalidUtf8FromRedirectedStdinIsAUsageError()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { typeof(CliArguments).Assembly.Location, "files", "write",
            "--session", "unused", "--path", "/tmp/input.txt", "--text-stdin", "--instance", "2147483647" })
            start.ArgumentList.Add(argument);

        var result = await RunProcessAsync(start, [0xff]);

        Assert.Equal(2, result.ExitCode);
        using var error = JsonDocument.Parse(result.Output);
        Assert.Equal("usage", error.RootElement.GetProperty("errorCode").GetString());
        Assert.StartsWith("Could not read standard input:", error.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task DoctorFindsTheMcpHostBesideTheCli()
    {
        // Installers merge the CLI and the MCP host into one folder so they share the
        // desktop's single .NET runtime. Resolving only the legacy sibling "agent"
        // folder made doctor report mcpHostExists=false on every real install.
        var directory = Path.Combine(Path.GetTempPath(), "surge-doctor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Copy the whole CLI build output and start it through the shared host:
            // the apphost executable needs native host DLLs that are not copied.
            var assembly = typeof(CliArguments).Assembly.Location;
            var source = Path.GetDirectoryName(assembly)!;
            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            }
            var host = Path.Combine(directory, "surgelink-agent-host.exe");
            File.WriteAllBytes(host, Array.Empty<byte>());

            var result = await RunAsyncFrom(directory, Path.GetFileName(assembly), "doctor", "--json");
            Assert.Equal(0, result.ExitCode);
            using var output = JsonDocument.Parse(result.Output);
            var data = output.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("mcpHostExists").GetBoolean());
            Assert.Equal(host, data.GetProperty("mcpHostPath").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return await RunProcessAsync(start);
    }

    /// <summary>Runs a CLI assembly copied elsewhere, resolving it through the shared host.</summary>
    private static Task<(int ExitCode, string Output)> RunAsyncFrom(string workingDirectory, string assemblyName, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        start.ArgumentList.Add(assemblyName);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return RunProcessAsync(start);
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(ProcessStartInfo start, byte[]? input = null)
    {
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(timeout.Token);
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.Equal("", await errors);
        return (process.ExitCode, await output);
    }
}
