using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Security;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Terminal;
using SurgeLink.Tests.Support;
using Xunit;

namespace SurgeLink.Protocols.Serial.Tests;

public sealed class SerialConnectionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TerminalTransfersRawBytesAndResizeDoesNotWriteControlSequences()
    {
        var port = new FakeSerialConnection();
        await using var session = await Connect(port);
        var terminal = session.Terminal!;
        var output = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.OutputReceived += (_, e) => output.TrySetResult(e.Data.ToArray());
        await terminal.StartAsync(new TerminalStartOptions { Columns = 80, Rows = 24 }, CancellationToken.None);
        await port.ReadStarted.Task.WaitAsync(TestTimeout);
        Assert.True(terminal.IsOpen);
        Assert.Empty(port.Writes); // No greeting, authentication or OS probes are sent.
        byte[] bytes = [13, 0, 10, 255, 27, 91, 65, 0xc3, 0xa9];
        port.Inject(bytes);
        Assert.Equal(bytes, await output.Task.WaitAsync(TestTimeout));
        await terminal.WriteAsync(bytes);
        Assert.Equal(bytes, Assert.Single(port.Writes));
        await terminal.ResizeAsync(100, 40);
        Assert.Single(port.Writes);
        Assert.Equal(100, terminal.Columns);
        Assert.Equal(40, terminal.Rows);
        Assert.Equal("COM3", (await session.LoadHostInfoAsync(CancellationToken.None)).HostName);
        Assert.Null(session.FileSystem);
        Assert.Null(session.Monitor);
    }

    [Fact]
    public async Task DisposeJoinsReaderReleasesPortOnceAndStopsCallbacks()
    {
        var port = new FakeSerialConnection();
        var session = await Connect(port);
        var terminal = session.Terminal!;
        int closed = 0;
        int output = 0;
        terminal.Closed += (_, _) => Interlocked.Increment(ref closed);
        terminal.OutputReceived += (_, _) => Interlocked.Increment(ref output);
        await terminal.StartAsync(new TerminalStartOptions(), CancellationToken.None);
        await port.ReadStarted.Task.WaitAsync(TestTimeout);
        await Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask()).WaitAsync(TestTimeout);
        port.Inject([1, 2, 3]);
        await Task.Delay(50);
        Assert.Equal(0, output);
        Assert.Equal(1, closed);
        Assert.Equal(1, port.DisposeCount);
        Assert.Equal(ConnectionState.Disconnected, session.State);
        Assert.False(terminal.IsOpen);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => terminal.WriteAsync(new byte[] { 1 }).AsTask());
    }

    [Fact]
    public async Task UnplugFaultsSessionAndAutomaticallyReleasesPort()
    {
        var port = new FakeSerialConnection { ReadFailure = new IOException("Device removed") };
        await using var session = await Connect(port);
        var faulted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Faulted += (_, message) => faulted.TrySetResult(message);
        await session.Terminal!.StartAsync(new TerminalStartOptions(), CancellationToken.None);
        Assert.Equal("Device removed", await faulted.Task.WaitAsync(TestTimeout));
        await port.Disposed.Task.WaitAsync(TestTimeout);
        Assert.Equal(ConnectionState.Faulted, session.State);
        Assert.False(session.Terminal.IsOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowOpenCanTimeoutOrCancelAndDisposesLateHandle(bool cancel)
    {
        using var release = new ManualResetEventSlim();
        var port = new FakeSerialConnection { OpenAction = () => release.Wait(TestTimeout) };
        var plugin = new SerialProtocolPlugin(_ => port);
        using var cancellation = new CancellationTokenSource();
        var connecting = plugin.ConnectAsync(SerialOptionsTests.Request() with
        { ConnectTimeout = cancel ? TestTimeout : TimeSpan.FromMilliseconds(100) }, Context(), cancellation.Token);
        await port.OpenStarted.Task.WaitAsync(TestTimeout);
        if (cancel) cancellation.Cancel();
        try
        {
            var error = await Assert.ThrowsAsync<ProtocolException>(() => connecting.WaitAsync(TestTimeout));
            Assert.Equal(cancel ? ProtocolFailureKind.Cancelled : ProtocolFailureKind.Timeout, error.Kind);
        }
        finally { release.Set(); }
        await port.Disposed.Task.WaitAsync(TestTimeout);
        Assert.Equal(1, port.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningFailureIsMappedAndReleasesHandle(bool occupied)
    {
        var port = new FakeSerialConnection { OpenAction = () =>
        {
            if (occupied) throw new UnauthorizedAccessException("Port in use");
            throw new IOException("Port missing");
        } };
        var error = await Assert.ThrowsAsync<ProtocolException>(() => Connect(port));
        Assert.Equal(occupied ? ProtocolFailureKind.ServerRejected : ProtocolFailureKind.Network, error.Kind);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotOpenDevice()
    {
        bool created = false;
        var plugin = new SerialProtocolPlugin(_ => { created = true; return new FakeSerialConnection(); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.ConnectAsync(SerialOptionsTests.Request(), Context(), cancellation.Token));
        Assert.False(created);
    }

    [Fact]
    public async Task DisposeWaitsForAnActiveWriteBeforeReleasingDevice()
    {
        using var writeStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var port = new FakeSerialConnection { WriteAction = _ => { writeStarted.Set(); release.Wait(TestTimeout); } };
        await using var session = await Connect(port);
        await session.Terminal!.StartAsync(new TerminalStartOptions(), CancellationToken.None);
        var writing = session.Terminal.WriteAsync(new byte[] { 1, 2, 3 }).AsTask();
        Assert.True(writeStarted.Wait(TestTimeout));
        var disposing = session.DisposeAsync().AsTask();
        try { Assert.False(disposing.IsCompleted); Assert.Equal(0, port.DisposeCount); }
        finally { release.Set(); }
        await Task.WhenAll(writing, disposing).WaitAsync(TestTimeout);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task WriteTimeoutFaultsAndReleasesConnection()
    {
        var port = new FakeSerialConnection { WriteAction = _ => throw new TimeoutException("CTS did not become ready") };
        await using var session = await Connect(port);
        await session.Terminal!.StartAsync(new TerminalStartOptions(), CancellationToken.None);
        var error = await Assert.ThrowsAsync<ProtocolException>(() => session.Terminal.WriteAsync(new byte[] { 1 }).AsTask());
        Assert.Equal(ProtocolFailureKind.Timeout, error.Kind);
        await port.Disposed.Task.WaitAsync(TestTimeout);
        Assert.Equal(ConnectionState.Faulted, session.State);
    }

    [Fact]
    public void DescriptorHasOnlyTerminalAndNoNetworkCredentials()
    {
        var descriptor = new SerialProtocolPlugin().Descriptor;
        Assert.Equal("serial", descriptor.Id);
        Assert.Equal(0, descriptor.DefaultPort);
        Assert.False(descriptor.UsesNetworkAddress);
        Assert.True(descriptor.AuthenticatesInTerminal);
        Assert.Equal(SessionCapabilities.Terminal, descriptor.Capabilities);
        Assert.NotNull(descriptor.DiscoverEndpoints);
    }

    private static Task<IProtocolSession> Connect(FakeSerialConnection port) =>
        new SerialProtocolPlugin(_ => port).ConnectAsync(SerialOptionsTests.Request(), Context(), CancellationToken.None);

    private static ProtocolHostContext Context() => new(NullLoggerFactory.Instance, new UnusedHostKeys());
    private sealed class UnusedHostKeys : IHostKeyStore
    {
        public bool TryGetFingerprint(string endpoint, out KnownHostEntry entry) => throw new InvalidOperationException();
        public void Save(KnownHostEntry entry) => throw new InvalidOperationException();
        public bool Remove(string endpoint) => throw new InvalidOperationException();
        public IReadOnlyList<KnownHostEntry> List() => throw new InvalidOperationException();
    }
}
