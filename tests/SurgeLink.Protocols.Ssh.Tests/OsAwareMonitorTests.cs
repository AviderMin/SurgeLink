using SurgeLink.Core.Protocols;
using SurgeLink.Protocols.Ssh;
using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class OsAwareMonitorTests
{
    private static readonly string LinuxOutput = TestMonitorOutput.Build(
        (MonitorScript.StatSection, "cpu 100 0 100 800 0 0 0 0\n"),
        (MonitorScript.MemInfoSection, "MemTotal: 1000 kB\nMemAvailable: 500 kB\n"));

    private static readonly string WindowsOutput = TestMonitorOutput.Build(
        (WindowsMonitorScript.CpuSection, "_Total|42.0\n0|42.0\n"),
        (WindowsMonitorScript.MemorySection, "total|2000\nfree|500\n"));

    [Fact]
    public async Task SampleAsyncSelectsTheLinuxProbeAndCachesTheDetection()
    {
        var detections = 0;
        var executor = TestCommandExecutor.Returning(command => command == MonitorScript.Command ? LinuxOutput : string.Empty);
        await using var monitor = new OsAwareMonitor(
            executor.ExecuteAsync,
            _ =>
            {
                detections++;
                return Task.FromResult(RemoteOperatingSystem.Linux);
            },
            null);

        var snapshot = await monitor.SampleAsync(CancellationToken.None);
        var second = await monitor.SampleAsync(CancellationToken.None);

        Assert.Equal(RemoteOperatingSystem.Linux, monitor.OperatingSystem);
        Assert.IsType<LinuxMonitorProbe>(monitor.ActiveProbe);
        Assert.Equal(0d, snapshot.Cpu.TotalPercent);
        Assert.Equal(1000L * 1024, snapshot.Memory.TotalBytes);
        Assert.Equal(500L * 1024, snapshot.Memory.AvailableBytes);
        Assert.Equal(0d, second.Cpu.TotalPercent);
        Assert.Equal(1, detections);
    }

    [Fact]
    public async Task SampleAsyncSelectsTheWindowsProbe()
    {
        var executor = TestCommandExecutor.Returning(command => command == WindowsMonitorScript.SampleCommand ? WindowsOutput : string.Empty);
        await using var monitor = new OsAwareMonitor(
            executor.ExecuteAsync,
            _ => Task.FromResult(RemoteOperatingSystem.Windows),
            null);

        var snapshot = await monitor.SampleAsync(CancellationToken.None);

        Assert.Equal(RemoteOperatingSystem.Windows, monitor.OperatingSystem);
        Assert.IsType<WindowsMonitorProbe>(monitor.ActiveProbe);
        Assert.Equal(42d, snapshot.Cpu.TotalPercent, 6);
        Assert.Equal(2000L * 1024, snapshot.Memory.TotalBytes);
        Assert.Equal(1500L * 1024, snapshot.Memory.UsedBytes);
    }

    [Fact]
    public async Task SampleAsyncThrowsForAnUnsupportedGuest()
    {
        var executor = TestCommandExecutor.Returning(_ => string.Empty);
        await using var monitor = new OsAwareMonitor(
            executor.ExecuteAsync,
            _ => Task.FromResult(RemoteOperatingSystem.Unknown),
            null);

        var exception = await Assert.ThrowsAsync<ProtocolException>(() => monitor.SampleAsync(CancellationToken.None));

        Assert.Equal(ProtocolFailureKind.Unknown, exception.Kind);
        Assert.Contains("Linux and Windows", exception.Message, StringComparison.Ordinal);
        Assert.Null(monitor.ActiveProbe);
        Assert.Equal(RemoteOperatingSystem.Unknown, monitor.OperatingSystem);
        Assert.Equal(0, executor.Count);
    }

    [Fact]
    public async Task DetectionUsesTheDefaultProbesWhenNoDetectorIsInjected()
    {
        var executor = new TestCommandExecutor(command =>
        {
            if (command == RemoteOsDetector.UnameCommand)
            {
                return Task.FromResult(new SshCommandResult(command, string.Empty, "'uname' is not recognized", 1));
            }

            if (command == RemoteOsDetector.WindowsVersionCommand)
            {
                return Task.FromResult(new SshCommandResult(command, "Microsoft Windows [Version 10.0.19045.3803]", string.Empty, 0));
            }

            return Task.FromResult(command == WindowsMonitorScript.SampleCommand
                ? new SshCommandResult(command, WindowsOutput, string.Empty, 0)
                : new SshCommandResult(command, string.Empty, string.Empty, 0));
        });
        await using var monitor = new OsAwareMonitor(executor.ExecuteAsync);

        var snapshot = await monitor.SampleAsync(CancellationToken.None);

        Assert.Equal(RemoteOperatingSystem.Windows, monitor.OperatingSystem);
        Assert.Equal(42d, snapshot.Cpu.TotalPercent, 6);
        Assert.Contains(RemoteOsDetector.UnameCommand, executor.Commands);
        Assert.Contains(RemoteOsDetector.WindowsVersionCommand, executor.Commands);
    }

    [Fact]
    public async Task GetTopProcessesIsRoutedToTheSelectedProbe()
    {
        var windowsCommands = WindowsMonitorScript.BuildTopProcessCommands(10);
        var executor = new TestCommandExecutor(command =>
        {
            if (command == windowsCommands[0])
            {
                return Task.FromResult(new SshCommandResult(command, "7|chrome|15.0|1024|3\n", string.Empty, 0));
            }

            return Task.FromResult(new SshCommandResult(command, string.Empty, string.Empty, 0));
        });
        await using var monitor = new OsAwareMonitor(
            executor.ExecuteAsync,
            _ => Task.FromResult(RemoteOperatingSystem.Windows),
            null);

        var processes = await monitor.GetTopProcessesAsync(10, CancellationToken.None);

        Assert.Single(processes);
        Assert.Equal("chrome", processes[0].Name);
        Assert.Equal(15d, processes[0].CpuPercent, 6);
    }

    [Fact]
    public async Task SampleAsyncPropagatesDetectorFailures()
    {
        var executor = TestCommandExecutor.Returning(_ => string.Empty);
        await using var monitor = new OsAwareMonitor(
            executor.ExecuteAsync,
            _ => Task.FromException<RemoteOperatingSystem>(new InvalidOperationException("probe failed")),
            null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.SampleAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SampleAsyncHonoursCancellation()
    {
        var executor = TestCommandExecutor.Returning(_ => string.Empty);
        await using var monitor = new OsAwareMonitor(
            executor.ExecuteAsync,
            _ => Task.FromResult(RemoteOperatingSystem.Linux),
            null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.SampleAsync(cancellation.Token));
    }

    [Fact]
    public void RecommendedIntervalIsTwoSeconds()
    {
        var executor = TestCommandExecutor.Returning(_ => string.Empty);
        var monitor = new OsAwareMonitor(executor.ExecuteAsync);

        Assert.Equal(TimeSpan.FromSeconds(2), monitor.RecommendedInterval);
        Assert.Equal(RemoteOperatingSystem.Unknown, monitor.OperatingSystem);
        Assert.Null(monitor.ActiveProbe);
    }

    [Fact]
    public void CreateReturnsTheOsAwareMonitor()
    {
        var executor = TestCommandExecutor.Returning(_ => string.Empty);

        var monitor = OsAwareMonitor.Create(executor.ExecuteAsync);

        Assert.IsType<OsAwareMonitor>(monitor);
    }

    [Fact]
    public async Task DisposedMonitorRejectsFurtherWork()
    {
        var executor = TestCommandExecutor.Returning(command => command == WindowsMonitorScript.SampleCommand ? WindowsOutput : string.Empty);
        var monitor = new OsAwareMonitor(
            executor.ExecuteAsync,
            _ => Task.FromResult(RemoteOperatingSystem.Windows),
            null);
        await monitor.SampleAsync(CancellationToken.None);

        await monitor.DisposeAsync();
        await monitor.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.SampleAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => monitor.GetTopProcessesAsync(5, CancellationToken.None));
    }

    [Fact]
    public void ConstructorRejectsNullExecutor()
    {
        Assert.Throws<ArgumentNullException>(() => new OsAwareMonitor(null!));
    }
}
