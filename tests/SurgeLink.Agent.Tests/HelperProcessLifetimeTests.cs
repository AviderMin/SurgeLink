using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using SurgeLink.AgentHost;
using SurgeLink.Cli;
using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class HelperProcessLifetimeTests
{
    [Fact]
    public async Task DesktopExitCancelsInFlightWorkWithoutCancellingTheCaller()
    {
        using var desktop = IdleProcess();
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = AgentProcessLifetime.RunAsync(desktop.Process.Id, async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 1;
        }, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        desktop.Stop();
        var error = await Assert.ThrowsAsync<AgentDesktopDiscoveryException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("desktop_instance_expired", error.ErrorCode);
        Assert.False(caller.IsCancellationRequested);
    }

    [Fact]
    public async Task FinishingARequestLeavesItsDesktopRunning()
    {
        using var desktop = IdleProcess();
        Assert.Equal(42, await AgentProcessLifetime.RunAsync(desktop.Process.Id, _ => Task.FromResult(42), default));
        Assert.False(desktop.Process.HasExited);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var operation = AgentProcessLifetime.RunAsync(Environment.ProcessId, async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 1;
        }, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task AutomaticHostWaitsUntilAllDesktopsHaveExited()
    {
        AgentDesktopProcess[] running = [new(10, null), new(20, null)];
        var connection = new DesktopConnection(discoverDesktops: () => Volatile.Read(ref running));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var lifetime = connection.WaitForDesktopExitAsync(cancellation.Token);
        Volatile.Write(ref running, [new(20, null)]);
        await Task.Delay(400, cancellation.Token);
        Assert.False(lifetime.IsCompleted);
        Volatile.Write(ref running, []);
        await lifetime.WaitAsync(cancellation.Token);
    }

    [Fact]
    public async Task SelectingAnotherDesktopMovesTheLifetimeBinding()
    {
        using var first = IdleProcess();
        using var second = IdleProcess();
        var secondPid = second.Process.Id;
        await using var endpoint = new AgentPipeServer(AgentEndpoint.ForProcess(secondPid), new AgentGateway());
        var connection = new DesktopConnection(first.Process.Id,
            discoverDesktops: () => [new(first.Process.Id, null), new(secondPid, null)]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var lifetime = connection.WaitForDesktopExitAsync(cancellation.Token);
        Assert.True((await connection.SendAsync(new("desktops.select", DesktopProcessId: secondPid), cancellation.Token)).Success);
        first.Stop();
        await Task.Delay(400, cancellation.Token);
        Assert.False(lifetime.IsCompleted);
        second.Stop();
        await lifetime.WaitAsync(cancellation.Token);
    }

    [Fact]
    public async Task IdleMcpExecutableExitsWithItsDesktopEvenWhenStdinStaysOpen()
    {
        using var desktop = IdleProcess();
        using var host = DotnetProcess(typeof(DesktopConnection).Assembly.Location, "--instance", desktop.Process.Id.ToString());
        var output = host.Process.StandardOutput.ReadToEndAsync();
        var errors = host.Process.StandardError.ReadToEndAsync();
        await Task.Delay(500);
        Assert.False(host.Process.HasExited);
        desktop.Stop();
        await host.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(0, host.Process.ExitCode);
        Assert.Equal("", await output);
        Assert.Equal("", await errors);
    }

    [Fact]
    public async Task CliExecutableExitsWhenItsDesktopDiesDuringAnUnansweredRequest()
    {
        using var desktop = IdleProcess();
        await using var pipe = new NamedPipeServerStream(AgentEndpoint.ForProcess(desktop.Process.Id), PipeDirection.InOut,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var cli = DotnetProcess(typeof(CliArguments).Assembly.Location, "sessions", "list", "--instance", desktop.Process.Id.ToString());
        var output = cli.Process.StandardOutput.ReadToEndAsync();
        var errors = cli.Process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await pipe.WaitForConnectionAsync(timeout.Token);
        Assert.Equal("sessions.list", (await AgentWire.ReadAsync<AgentRequest>(pipe, timeout.Token)).Method);
        desktop.Stop();
        // Keep the pipe open without a response: process exit must cancel the read.
        await cli.Process.WaitForExitAsync(timeout.Token);
        Assert.Equal(3, cli.Process.ExitCode);
        using var result = JsonDocument.Parse(await output);
        Assert.Equal("desktop_instance_expired", result.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("", await errors);
    }

    [Fact]
    public async Task InitializedMcpExecutableExitsWhenItsBoundDesktopCloses()
    {
        using var desktop = IdleProcess();
        await using var endpoint = new AgentPipeServer(AgentEndpoint.ForProcess(desktop.Process.Id), new AgentGateway());
        using var host = DotnetProcess(typeof(DesktopConnection).Assembly.Location, "--instance", desktop.Process.Id.ToString());
        var errors = host.Process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await host.Process.StandardInput.WriteLineAsync("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"lifetime-test","version":"1"}}}
            """);
        await host.Process.StandardInput.FlushAsync(timeout.Token);
        var initialized = await host.Process.StandardOutput.ReadLineAsync(timeout.Token);
        using (var response = JsonDocument.Parse(initialized!))
        {
            Assert.True(response.RootElement.TryGetProperty("result", out _), initialized);
        }
        await host.Process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await host.Process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        await host.Process.StandardInput.FlushAsync(timeout.Token);
        var tools = await host.Process.StandardOutput.ReadLineAsync(timeout.Token);
        using (var response = JsonDocument.Parse(tools!))
        {
            Assert.True(response.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() > 0);
        }
        var remainingOutput = host.Process.StandardOutput.ReadToEndAsync();
        desktop.Stop();
        await host.Process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, host.Process.ExitCode);
        Assert.Equal("", await remainingOutput);
        Assert.Equal("", await errors);
    }

    [Fact]
    public async Task BoundHostExitsWithoutStoppingAnotherDesktop()
    {
        using var first = IdleProcess();
        using var other = IdleProcess();
        using var connection = new DesktopConnection(first.Process.Id,
            discoverDesktops: () => [new(first.Process.Id, null), new(other.Process.Id, null)]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var lifetime = connection.WaitForDesktopExitAsync(timeout.Token);
        first.Stop();
        await lifetime.WaitAsync(timeout.Token);
        Assert.False(other.Process.HasExited);
    }

    [Fact]
    public async Task ShutdownRemovesPresenceEvenWhenHeartbeatResponseNeverArrives()
    {
        var pid = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000000, int.MaxValue);
        var name = AgentEndpoint.ForProcess(pid);
        await using var heartbeat = new NamedPipeServerStream(name, PipeDirection.InOut, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var disconnect = new NamedPipeServerStream(name, PipeDirection.InOut, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var connection = new DesktopConnection(pid, discoverDesktops: () => [new(pid, null)]);
        using var shutdown = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var gateway = new AgentGateway();
        var presence = connection.RunPresenceAsync(shutdown.Token);
        connection.ClientConnected("test");
        await heartbeat.WaitForConnectionAsync(timeout.Token);
        var request = await AgentWire.ReadAsync<AgentRequest>(heartbeat, timeout.Token);
        Assert.True((await gateway.ExecuteAsync(request, timeout.Token)).Success);
        Assert.Single(gateway.McpPeers);
        // Hold the heartbeat response open after registration, then shut down.
        await shutdown.CancelAsync();
        await disconnect.WaitForConnectionAsync(timeout.Token);
        var removal = await AgentWire.ReadAsync<AgentRequest>(disconnect, timeout.Token);
        Assert.Equal("client.disconnect", removal.Method);
        await AgentWire.WriteAsync(disconnect, await gateway.ExecuteAsync(removal, timeout.Token), timeout.Token);
        await presence.WaitAsync(timeout.Token);
        Assert.Empty(gateway.McpPeers);
    }

    internal static ChildProcess IdleProcess() => OperatingSystem.IsWindows()
        ? new("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60")
        : new("sleep", "60");

    private static ChildProcess DotnetProcess(params string[] arguments) => new("dotnet", arguments);

    internal sealed class ChildProcess : IDisposable
    {
        public Process Process { get; }

        public ChildProcess(string executable, params string[] arguments)
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
            Process = Process.Start(start) ?? throw new InvalidOperationException("Could not start test process.");
        }

        public void Stop()
        {
            if (!Process.HasExited) { Process.Kill(entireProcessTree: true); }
            Process.WaitForExit();
        }

        public void Dispose()
        {
            Stop();
            Process.Dispose();
        }
    }
}
