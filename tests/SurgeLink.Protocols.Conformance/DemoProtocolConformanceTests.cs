using System.Text;
using SurgeLink.Core.Files;
using SurgeLink.Core.Monitoring;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Protocols.Conformance;

/// <summary>Demo-specific checks that the generic suite cannot express (failure path, shell echo, seeded data).</summary>
public sealed class DemoProtocolConformanceTests
{
    [Fact]
    public async Task ExitClosesTerminalAndDisconnectsSessionOnce()
    {
        await using var connection = await ConformanceSession.OpenAsync(ProtocolConformanceRegistry.Get("demo"));
        var terminal = connection.Session.Terminal!;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closedCount = 0;
        terminal.Closed += (_, _) => { Interlocked.Increment(ref closedCount); closed.TrySetResult(); };
        await terminal.StartAsync(new TerminalStartOptions { Columns = 80, Rows = 24 }, CancellationToken.None);
        await terminal.WriteAsync(Encoding.UTF8.GetBytes("exit\recho must-not-run\r"));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(terminal.IsOpen);
        Assert.Equal(SurgeLink.Core.Sessions.ConnectionState.Disconnected, connection.Session.State);
        await connection.Session.DisposeAsync();
        Assert.Equal(1, closedCount);
    }

    [Fact]
    public async Task ConnectingToFailHostThrowsNetworkProtocolException()
    {
        IProtocolUnderTest fixture = ProtocolConformanceRegistry.Get("demo");
        ConnectionRequest request = fixture.CreateConnectionRequest() with { Host = "fail" };
        var host = new ConformanceHost();

        ProtocolException exception = await Assert.ThrowsAsync<ProtocolException>(
            () => ConformanceConnector.ConnectAsync(
                fixture.Plugin,
                request,
                host.Context,
                ProtocolConformanceSuite.DefaultOperationTimeout));

        Assert.Equal(ProtocolFailureKind.Network, exception.Kind);
    }

    [Fact]
    public async Task TerminalEchoesTypedCommand()
    {
        IProtocolUnderTest fixture = ProtocolConformanceRegistry.Get("demo");
        await using ConformanceSession connection = await ConformanceSession.OpenAsync(fixture);
        IRemoteTerminal terminal = connection.Session.Terminal!;

        using var capture = new TerminalCapture(terminal);
        await terminal.StartAsync(
            new TerminalStartOptions { Columns = 80, Rows = 24, TerminalType = "xterm-256color" },
            CancellationToken.None);
        await terminal.WriteAsync(Encoding.UTF8.GetBytes("echo conformance-demo\r"), CancellationToken.None);

        await capture.WaitForTextAsync("conformance-demo", TimeSpan.FromSeconds(5));
        Assert.Contains("demo@surge", capture.Text);
    }

    [Fact]
    public async Task FileSystemExposesSeededDemoFiles()
    {
        IProtocolUnderTest fixture = ProtocolConformanceRegistry.Get("demo");
        await using ConformanceSession connection = await ConformanceSession.OpenAsync(fixture);
        IRemoteFileSystem fileSystem = connection.Session.FileSystem!;

        string home = await fileSystem.GetHomeDirectoryAsync(CancellationToken.None);
        IReadOnlyList<RemoteFileEntry> entries = await fileSystem.ListAsync(home, CancellationToken.None);

        Assert.Equal("/home/demo", home);
        Assert.Contains(entries, entry => entry.Name == "demo.txt" && entry.Kind == RemoteEntryKind.File);
        Assert.Contains(entries, entry => entry.Name == "Documents" && entry.Kind == RemoteEntryKind.Directory);
    }

    [Fact]
    public async Task MonitorProvidesSyntheticMetrics()
    {
        IProtocolUnderTest fixture = ProtocolConformanceRegistry.Get("demo");
        await using ConformanceSession connection = await ConformanceSession.OpenAsync(fixture);
        IRemoteSystemMonitor monitor = connection.Session.Monitor!;

        SystemSnapshot snapshot = await monitor.SampleAsync(CancellationToken.None);
        ConformanceAssert.AssertSaneSnapshot(snapshot);
        Assert.Equal(8, snapshot.Cpu.PerCorePercent.Count);
        Assert.Equal(3, snapshot.Disks.Count);

        IReadOnlyList<RemoteProcessInfo> processes = await monitor.GetTopProcessesAsync(2, CancellationToken.None);
        Assert.Equal(2, processes.Count);
        Assert.All(processes, process => Assert.True(process.ProcessId > 0));
    }
}
