using System.Text;
using SurgeLink.Core.Monitoring;
using SurgeLink.Core.Protocols;
using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class LinuxMonitorProbeTests
{
    private const string StatFirstSample = "cpu 100 0 100 800 0 0 0 0\ncpu0 100 0 100 800 0 0 0 0\n";

    private const string StatSecondSample = "cpu 200 0 200 1600 0 0 0 0\ncpu0 200 0 200 1600 0 0 0 0\n";

    private const string MemInfo = """
        MemTotal:       1000000 kB
        MemFree:         100000 kB
        MemAvailable:    250000 kB
        Buffers:          50000 kB
        Cached:          100000 kB
        SwapTotal:       200000 kB
        SwapFree:         50000 kB
        """;

    private const string NetDev = """
        Inter-|   Receive                                                |  Transmit
         face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
            lo:      100       1    0    0    0     0          0         0      100       1    0    0    0     0       0          0
          eth0:     2000      20    0    0    0     0          0         0     3000      30    0    0    0     0       0          0
        """;

    private const string Df = """
        Filesystem     1024-blocks      Used Available Capacity Mounted on
        /dev/sda1             1000       200        800      20% /
        tmpfs               500000         0     500000       0% /dev/shm
        """;

    private const string DiskStats = """
           8       0 sda 100 0 2000 30 400 0 5000 60 1 70 80
        """;

    private const string TopProcessesPsEo = """
        PID USER     %CPU %MEM   RSS COMMAND
          1 root      0.0  0.1  1672 systemd
        412 alice    42.0  1.5 90000 node
        """;

    [Fact]
    public async Task SampleAsyncBuildsAFullSnapshot()
    {
        var executor = new FakeExecutor(_ => Task.FromResult(new SshCommandResult(
            MonitorScript.Command,
            BuildOutput(StatFirstSample, MemInfo, "1.50 2.50 3.50 4/500 12345\n", "86400.00 100.00\n", NetDev, DiskStats, Df),
            string.Empty,
            0)));
        await using var probe = new LinuxMonitorProbe(executor.ExecuteAsync);

        var snapshot = await probe.SampleAsync(CancellationToken.None);

        Assert.Equal(0d, snapshot.Cpu.TotalPercent);
        Assert.Single(snapshot.Cpu.PerCorePercent);
        Assert.Equal(0d, snapshot.Cpu.PerCorePercent[0]);
        Assert.Equal(1.5, snapshot.Cpu.Load1, 6);
        Assert.Equal(2.5, snapshot.Cpu.Load5, 6);
        Assert.Equal(3.5, snapshot.Cpu.Load15, 6);
        Assert.Equal(4, snapshot.Cpu.ProcessCount);
        Assert.Equal(500, snapshot.Cpu.ThreadCount);
        Assert.Equal(1000000L * 1024, snapshot.Memory.TotalBytes);
        Assert.Equal(250000L * 1024, snapshot.Memory.AvailableBytes);
        Assert.Equal(150000L * 1024, snapshot.Memory.SwapUsedBytes);
        Assert.Equal(TimeSpan.FromDays(1), snapshot.Uptime);
        Assert.Single(snapshot.Disks);
        Assert.Equal("/", snapshot.Disks[0].MountPoint);
        Assert.Equal(2, snapshot.Networks.Count);
        Assert.Equal("eth0", snapshot.Networks[1].InterfaceName);
        Assert.Single(probe.LastDiskIoStatistics);
        Assert.Equal("sda", probe.LastDiskIoStatistics[0].DeviceName);
        Assert.Single(executor.Commands);
        Assert.Equal(MonitorScript.Command, executor.Commands[0]);
    }

    [Fact]
    public async Task SecondSampleUsesTheStoredCpuDelta()
    {
        var stat = StatFirstSample;
        var executor = new FakeExecutor(_ => Task.FromResult(new SshCommandResult(
            MonitorScript.Command,
            BuildOutput(stat, MemInfo, "0.10 0.20 0.30 1/100 1\n", string.Empty, NetDev, DiskStats, Df),
            string.Empty,
            0)));
        await using var probe = new LinuxMonitorProbe(executor.ExecuteAsync);

        var first = await probe.SampleAsync(CancellationToken.None);
        stat = StatSecondSample;
        var second = await probe.SampleAsync(CancellationToken.None);

        Assert.Equal(0d, first.Cpu.TotalPercent);
        Assert.Equal(20d, second.Cpu.TotalPercent, 6);
        Assert.Equal(20d, second.Cpu.PerCorePercent[0], 6);
    }

    [Fact]
    public async Task SampleAsyncThrowsWhenNoSectionIsReturned()
    {
        var executor = new FakeExecutor(_ => Task.FromResult(new SshCommandResult(
            MonitorScript.Command,
            string.Empty,
            "sh: not found",
            127)));
        await using var probe = new LinuxMonitorProbe(executor.ExecuteAsync);

        var exception = await Assert.ThrowsAsync<ProtocolException>(() => probe.SampleAsync(CancellationToken.None));

        Assert.Equal(ProtocolFailureKind.Unknown, exception.Kind);
        Assert.Contains("did not return any usable section", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SampleAsyncMapsExecutorFailures()
    {
        var executor = new FakeExecutor(_ => Task.FromException<SshCommandResult>(new Renci.SshNet.Common.SshConnectionException("socket closed")));
        await using var probe = new LinuxMonitorProbe(executor.ExecuteAsync);

        var exception = await Assert.ThrowsAsync<ProtocolException>(() => probe.SampleAsync(CancellationToken.None));

        Assert.Equal(ProtocolFailureKind.Network, exception.Kind);
    }

    [Fact]
    public async Task SampleAsyncDoesNotThrowOnGarbageSections()
    {
        var executor = new FakeExecutor(_ => Task.FromResult(new SshCommandResult(
            MonitorScript.Command,
            BuildOutput("garbage", "garbage", "garbage", "garbage", "garbage", "garbage", "garbage"),
            string.Empty,
            0)));
        await using var probe = new LinuxMonitorProbe(executor.ExecuteAsync);

        var snapshot = await probe.SampleAsync(CancellationToken.None);

        Assert.Equal(0d, snapshot.Cpu.TotalPercent);
        Assert.Empty(snapshot.Cpu.PerCorePercent);
        Assert.Equal(0, snapshot.Memory.TotalBytes);
        Assert.Empty(snapshot.Disks);
        Assert.Empty(snapshot.Networks);
        Assert.Null(snapshot.Uptime);
    }

    [Fact]
    public async Task GetTopProcessesFallsBackToLaterCommands()
    {
        var commands = new List<string>();
        var executor = new FakeExecutor(command =>
        {
            commands.Add(command);
            return Task.FromResult(command.StartsWith("LC_ALL=C ps aux", StringComparison.Ordinal)
                ? new SshCommandResult(command, TopProcessesPsEo, string.Empty, 0)
                : new SshCommandResult(command, string.Empty, "unsupported", 1));
        });
        await using var probe = new LinuxMonitorProbe(executor.ExecuteAsync);

        var processes = await probe.GetTopProcessesAsync(10, CancellationToken.None);

        Assert.Equal(2, processes.Count);
        Assert.Equal(42d, processes[0].CpuPercent, 6);
        Assert.Equal(3, commands.Count);
        Assert.StartsWith("LC_ALL=C ps aux", commands[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTopProcessesReturnsEmptyWhenEveryCommandFails()
    {
        var executor = new FakeExecutor(command => Task.FromResult(new SshCommandResult(command, string.Empty, "denied", 1)));
        await using var probe = new LinuxMonitorProbe(executor.ExecuteAsync);

        var processes = await probe.GetTopProcessesAsync(10, CancellationToken.None);

        Assert.Empty(processes);
    }

    [Fact]
    public async Task GetTopProcessesCapsTheResult()
    {
        var executor = new FakeExecutor(command => Task.FromResult(new SshCommandResult(command, TopProcessesPsEo, string.Empty, 0)));
        await using var probe = new LinuxMonitorProbe(executor.ExecuteAsync);

        var processes = await probe.GetTopProcessesAsync(1, CancellationToken.None);

        Assert.Single(processes);
        Assert.Equal(412, processes[0].ProcessId);
    }

    [Fact]
    public async Task GetTopProcessesReturnsEmptyForNonPositiveCount()
    {
        var executor = new FakeExecutor(command => Task.FromResult(new SshCommandResult(command, TopProcessesPsEo, string.Empty, 0)));
        await using var probe = new LinuxMonitorProbe(executor.ExecuteAsync);

        Assert.Empty(await probe.GetTopProcessesAsync(0, CancellationToken.None));
    }

    [Fact]
    public void RecommendedIntervalIsTwoSeconds()
    {
        var executor = new FakeExecutor(_ => Task.FromResult(new SshCommandResult(MonitorScript.Command, string.Empty, string.Empty, 0)));
        var probe = new LinuxMonitorProbe(executor.ExecuteAsync);

        Assert.Equal(TimeSpan.FromSeconds(2), probe.RecommendedInterval);
    }

    [Fact]
    public async Task SampleAsyncAfterDisposeThrows()
    {
        var executor = new FakeExecutor(_ => Task.FromResult(new SshCommandResult(MonitorScript.Command, string.Empty, string.Empty, 0)));
        var probe = new LinuxMonitorProbe(executor.ExecuteAsync);
        await probe.DisposeAsync();
        await probe.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => probe.SampleAsync(CancellationToken.None));
    }

    [Fact]
    public void ConstructorRejectsNullExecutor()
    {
        Assert.Throws<ArgumentNullException>(() => new LinuxMonitorProbe(null!));
    }

    private static string BuildOutput(
        string stat,
        string memInfo,
        string loadAvg,
        string uptime,
        string netDev,
        string diskStats,
        string df)
    {
        var builder = new StringBuilder();
        Append(MonitorScript.StatSection, stat);
        Append(MonitorScript.MemInfoSection, memInfo);
        Append(MonitorScript.LoadAvgSection, loadAvg);
        Append(MonitorScript.UptimeSection, uptime);
        Append(MonitorScript.NetDevSection, netDev);
        Append(MonitorScript.DiskStatsSection, diskStats);
        Append(MonitorScript.DiskUsageSection, df);
        return builder.ToString();

        void Append(string section, string content)
        {
            builder.Append(MonitorOutput.MarkerPrefix).Append(section).Append('\n');
            if (content.Length > 0)
            {
                builder.Append(content);
                if (!content.EndsWith('\n'))
                {
                    builder.Append('\n');
                }
            }
        }
    }

    private sealed class FakeExecutor
    {
        private readonly Func<string, Task<SshCommandResult>> _handler;

        public FakeExecutor(Func<string, Task<SshCommandResult>> handler) => _handler = handler;

        public List<string> Commands { get; } = new();

        public Task<SshCommandResult> ExecuteAsync(string command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            cancellationToken.ThrowIfCancellationRequested();
            return _handler(command);
        }
    }
}