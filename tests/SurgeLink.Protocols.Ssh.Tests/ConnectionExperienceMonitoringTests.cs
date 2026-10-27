using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class ConnectionExperienceMonitoringTests
{
    [Fact]
    public async Task LocalizedProcessListingKeepsResourceMetricsAndDoesNotFallback()
    {
        const string localizedPsOutput = """
            PID BENUTZER  %CPU %MEM   RSS BEFEHL
              1 root       0,0  0,1  1672 systemd
            412 alice     42,0  1,5 90000 node
            """;
        const string invariantPsOutput = """
            PID USER     %CPU %MEM   RSS COMMAND
              1 root      0.0  0.1  1672 systemd
            412 alice    42.0  1.5 90000 node
            """;
        var commands = new List<string>();
        Task<SshCommandResult> ExecuteAsync(string command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            commands.Add(command);
            var output = command.StartsWith("LC_ALL=C ps ", StringComparison.Ordinal)
                ? invariantPsOutput
                : localizedPsOutput;
            return Task.FromResult(new SshCommandResult(command, output, string.Empty, 0));
        }

        await using var probe = new LinuxMonitorProbe(ExecuteAsync);
        var processes = await probe.GetTopProcessesAsync(10, CancellationToken.None);

        Assert.Single(commands);
        Assert.StartsWith("LC_ALL=C ps ", commands[0], StringComparison.Ordinal);
        Assert.Equal(2, processes.Count);
        Assert.Equal(42d, processes[0].CpuPercent, 6);
        Assert.Equal(1.5d, processes[0].MemoryPercent, 6);
        Assert.Equal(90000L * 1024, processes[0].ResidentBytes);
    }
}
