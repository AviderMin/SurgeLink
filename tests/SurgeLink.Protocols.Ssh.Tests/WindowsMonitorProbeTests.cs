using System.Text;
using SurgeLink.Core.Monitoring;
using SurgeLink.Core.Protocols;
using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class WindowsMonitorProbeTests
{
    private static readonly string BootTime = DateTimeOffset.UtcNow.AddHours(-2).ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly string FullOutput = TestMonitorOutput.Build(
        (WindowsMonitorScript.CpuSection, "_Total|25.0\n0|20.0\n1|30.0\n"),
        (WindowsMonitorScript.ProcessorSection, "40|4|2\n"),
        (WindowsMonitorScript.MemorySection, "total|16318456\nfree|1732544\nvirtualTotal|20971520\nvirtualFree|10485760\n"),
        (WindowsMonitorScript.SystemSection, "processes|123\nthreads|4567\nqueue|2\nboot|" + BootTime + "\n"),
        (WindowsMonitorScript.DiskSection, "C:|536870912000|268435456000|NTFS\nD:|107374182400|107374182400|\n"),
        (WindowsMonitorScript.NetworkSection, "Ethernet|1000|2000|10|20\n"),
        (WindowsMonitorScript.NetworkFallbackSection, "Wifi|3000|4000|30|40\n"),
        (WindowsMonitorScript.EndSection, string.Empty));

    [Fact]
    public async Task SampleAsyncBuildsAFullSnapshot()
    {
        var executor = TestCommandExecutor.Returning(_ => FullOutput);
        await using var probe = new WindowsMonitorProbe(executor.ExecuteAsync);

        var snapshot = await probe.SampleAsync(CancellationToken.None);

        Assert.Equal(25d, snapshot.Cpu.TotalPercent, 6);
        Assert.Equal(new[] { 20d, 30d }, snapshot.Cpu.PerCorePercent);
        Assert.Equal(123, snapshot.Cpu.ProcessCount);
        Assert.Equal(4567, snapshot.Cpu.ThreadCount);
        Assert.Equal(16318456L * 1024, snapshot.Memory.TotalBytes);
        Assert.Equal(1732544L * 1024, snapshot.Memory.AvailableBytes);
        Assert.Equal(2, snapshot.Disks.Count);
        Assert.Equal("C:", snapshot.Disks[0].MountPoint);
        Assert.Single(snapshot.Networks);
        Assert.Equal("Ethernet", snapshot.Networks[0].InterfaceName);
        Assert.NotNull(snapshot.Uptime);
        Assert.InRange(snapshot.Uptime!.Value, TimeSpan.FromMinutes(119), TimeSpan.FromMinutes(121));
        Assert.Single(executor.Commands);
        Assert.Equal(WindowsMonitorScript.SampleCommand, executor.Commands[0]);
        Assert.True((DateTimeOffset.UtcNow - snapshot.TimestampUtc).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task SampleAsyncUsesTheRawNetworkFallbackWhenTheCmdletIsMissing()
    {
        var output = TestMonitorOutput.Build(
            (WindowsMonitorScript.CpuSection, "_Total|10.0\n"),
            (WindowsMonitorScript.NetworkFallbackSection, "Wifi|3000|4000|30|40\n"));
        var executor = TestCommandExecutor.Returning(_ => output);
        await using var probe = new WindowsMonitorProbe(executor.ExecuteAsync);

        var snapshot = await probe.SampleAsync(CancellationToken.None);

        Assert.Single(snapshot.Networks);
        Assert.Equal("Wifi", snapshot.Networks[0].InterfaceName);
        Assert.Equal(3000, snapshot.Networks[0].BytesReceived);
    }

    [Fact]
    public async Task SampleAsyncDegradesToZerosWhenSectionsAreMissing()
    {
        var output = TestMonitorOutput.Build((WindowsMonitorScript.EndSection, string.Empty));
        var executor = TestCommandExecutor.Returning(_ => output);
        await using var probe = new WindowsMonitorProbe(executor.ExecuteAsync);

        var snapshot = await probe.SampleAsync(CancellationToken.None);

        Assert.Equal(0d, snapshot.Cpu.TotalPercent);
        Assert.Empty(snapshot.Cpu.PerCorePercent);
        Assert.Equal(0, snapshot.Memory.TotalBytes);
        Assert.Empty(snapshot.Disks);
        Assert.Empty(snapshot.Networks);
        Assert.Null(snapshot.Uptime);
    }

    [Fact]
    public async Task SampleAsyncThrowsWhenNoSectionIsReturned()
    {
        var executor = TestCommandExecutor.Returning(_ => string.Empty);
        await using var probe = new WindowsMonitorProbe(executor.ExecuteAsync);

        var exception = await Assert.ThrowsAsync<ProtocolException>(() => probe.SampleAsync(CancellationToken.None));

        Assert.Equal(ProtocolFailureKind.Unknown, exception.Kind);
        Assert.Contains("did not return any usable section", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SampleAsyncMapsExecutorFailures()
    {
        var executor = new TestCommandExecutor((_, _) =>
            Task.FromException<SshCommandResult>(new Renci.SshNet.Common.SshConnectionException("socket closed")));
        await using var probe = new WindowsMonitorProbe(executor.ExecuteAsync);

        var exception = await Assert.ThrowsAsync<ProtocolException>(() => probe.SampleAsync(CancellationToken.None));

        Assert.Equal(ProtocolFailureKind.Network, exception.Kind);
    }

    [Fact]
    public async Task GetTopProcessesUsesThePerfCommandFirst()
    {
        var commands = WindowsMonitorScript.BuildTopProcessCommands(10);
        var executor = new TestCommandExecutor(command => Task.FromResult(new SshCommandResult(
            command,
            command == commands[0] ? "100|chrome#1|12.5|1048576|40\n200|notepad|0.0|2048|4\n" : "9|fallback|1.0|1|1\n",
            string.Empty,
            0)));
        await using var probe = new WindowsMonitorProbe(executor.ExecuteAsync);

        var processes = await probe.GetTopProcessesAsync(10, CancellationToken.None);

        Assert.Equal(2, processes.Count);
        Assert.Equal("chrome", processes[0].Name);
        Assert.Single(executor.Commands);
    }

    [Fact]
    public async Task GetTopProcessesFallsBackToGetProcess()
    {
        var commands = WindowsMonitorScript.BuildTopProcessCommands(10);
        var executor = new TestCommandExecutor(command => Task.FromResult(new SshCommandResult(
            command,
            command == commands[1] ? "300|fallback|7.5|4096|3\n" : string.Empty,
            string.Empty,
            0)));
        await using var probe = new WindowsMonitorProbe(executor.ExecuteAsync);

        var processes = await probe.GetTopProcessesAsync(10, CancellationToken.None);

        Assert.Single(processes);
        Assert.Equal("fallback", processes[0].Name);
        Assert.Equal(2, executor.Count);
    }

    [Fact]
    public async Task GetTopProcessesCapsTheResult()
    {
        var commands = WindowsMonitorScript.BuildTopProcessCommands(2);
        var output = "1|alpha|1.0|1|1\n2|beta|50.0|1|1\n3|gamma|10.0|1|1\n";
        var executor = TestCommandExecutor.Returning(command => command == commands[0] ? output : string.Empty);
        await using var probe = new WindowsMonitorProbe(executor.ExecuteAsync);

        var processes = await probe.GetTopProcessesAsync(2, CancellationToken.None);

        Assert.Equal(2, processes.Count);
        Assert.Equal("beta", processes[0].Name);
        Assert.Equal("gamma", processes[1].Name);
    }

    [Fact]
    public async Task GetTopProcessesReturnsEmptyWhenEveryCommandFails()
    {
        var executor = new TestCommandExecutor((_, _) => Task.FromException<SshCommandResult>(new InvalidOperationException("no")));
        await using var probe = new WindowsMonitorProbe(executor.ExecuteAsync);

        Assert.Empty(await probe.GetTopProcessesAsync(5, CancellationToken.None));
    }

    [Fact]
    public async Task GetTopProcessesReturnsEmptyForNonPositiveCount()
    {
        var executor = TestCommandExecutor.Returning(_ => "1|x|1|1|1\n");
        await using var probe = new WindowsMonitorProbe(executor.ExecuteAsync);

        Assert.Empty(await probe.GetTopProcessesAsync(0, CancellationToken.None));
        Assert.Empty(await probe.GetTopProcessesAsync(-2, CancellationToken.None));
        Assert.Equal(0, executor.Count);
    }

    [Fact]
    public void RecommendedIntervalIsTwoSeconds()
    {
        var executor = TestCommandExecutor.Returning(_ => string.Empty);
        var probe = new WindowsMonitorProbe(executor.ExecuteAsync);

        Assert.Equal(TimeSpan.FromSeconds(2), probe.RecommendedInterval);
    }

    [Fact]
    public async Task DisposedProbeRejectsSampling()
    {
        var executor = TestCommandExecutor.Returning(_ => string.Empty);
        var probe = new WindowsMonitorProbe(executor.ExecuteAsync);

        await probe.DisposeAsync();
        await probe.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => probe.SampleAsync(CancellationToken.None));
    }

    [Fact]
    public void ConstructorRejectsNullExecutor()
    {
        Assert.Throws<ArgumentNullException>(() => new WindowsMonitorProbe(null!));
    }
}