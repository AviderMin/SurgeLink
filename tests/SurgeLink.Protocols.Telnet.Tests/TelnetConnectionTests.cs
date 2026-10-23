using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Security;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Terminal;
using Xunit;

namespace SurgeLink.Protocols.Telnet.Tests;

public sealed class TelnetConnectionTests
{
    [Fact]
    public async Task ConnectNegotiatesTransfersBytesResizesAndDetectsRemoteClose()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var session = await ConnectAsync(listener, timeout.Token);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = peer.GetStream();
        var terminal = session.Terminal!;
        var output = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<byte>();
        int closeCount = 0;
        terminal.OutputReceived += (_, e) =>
        {
            received.AddRange(e.Data.ToArray());
            if (received.Count >= 9) output.TrySetResult(received.ToArray());
        };
        terminal.Closed += (_, _) => { Interlocked.Increment(ref closeCount); closed.TrySetResult(); };
        await terminal.StartAsync(new TerminalStartOptions { Columns = 80, Rows = 24 }, timeout.Token);
        Assert.True(terminal.IsOpen);
        await ReadExactlyAsync(stream, 21, timeout.Token); // Initial WILL/DO offers.

        // Fragmented negotiation must not leak into the terminal text.
        await stream.WriteAsync(new byte[] { 255, 253 }, timeout.Token);
        await stream.WriteAsync(new byte[] { 31, 255, 251, 1 }, timeout.Token);
        Assert.Equal(new byte[] { 255, 250, 31, 0, 80, 0, 24, 255, 240 }, await ReadExactlyAsync(stream, 9, timeout.Token));
        await stream.WriteAsync(Encoding.UTF8.GetBytes("login: \r\0\n"), timeout.Token);
        Assert.Equal(Encoding.UTF8.GetBytes("login: \r\n"), await output.Task.WaitAsync(timeout.Token));

        await terminal.WriteAsync(Encoding.UTF8.GetBytes("alice\r"), timeout.Token);
        Assert.Equal(Encoding.UTF8.GetBytes("alice\r\0"), await ReadExactlyAsync(stream, 7, timeout.Token));
        await terminal.ResizeAsync(255, 40, timeout.Token);
        Assert.Equal(new byte[] { 255, 250, 31, 0, 255, 255, 0, 40, 255, 240 }, await ReadExactlyAsync(stream, 10, timeout.Token));

        Assert.Equal("127.0.0.1", (await session.LoadHostInfoAsync(timeout.Token)).HostName);
        Assert.Null(session.FileSystem);
        Assert.Null(session.Monitor);
        peer.Dispose();
        await closed.Task.WaitAsync(timeout.Token);
        Assert.Equal(ConnectionState.Disconnected, session.State);
        Assert.False(terminal.IsOpen);
        await session.DisposeAsync();
        await session.DisposeAsync();
        Assert.Equal(1, closeCount);
    }

    [Fact]
    public async Task DisposeStopsReaderAndClosesSocketWithoutLateCallbacks()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var session = await ConnectAsync(listener, timeout.Token);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var terminal = session.Terminal!;
        int callbacks = 0;
        terminal.OutputReceived += (_, _) => Interlocked.Increment(ref callbacks);
        terminal.Closed += (_, _) => Interlocked.Increment(ref callbacks);
        session.StateChanged += (_, _) => Interlocked.Increment(ref callbacks);
        await terminal.StartAsync(new TerminalStartOptions(), timeout.Token);
        await ReadExactlyAsync(peer.GetStream(), 21, timeout.Token);
        await Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask()).WaitAsync(timeout.Token);
        int after = Volatile.Read(ref callbacks);
        await Task.Delay(50, timeout.Token);
        Assert.Equal(after, Volatile.Read(ref callbacks));
        Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], timeout.Token));
        Assert.Equal(ConnectionState.Disconnected, session.State);
        Assert.False(terminal.IsOpen);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => terminal.WriteAsync(new byte[] { 1 }).AsTask());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task InvalidPortIsConfigurationFailure(int port)
    {
        var request = new ConnectionRequest { Host = "127.0.0.1", Port = port, Username = "" };
        var error = await Assert.ThrowsAsync<ProtocolException>(() => new TelnetProtocolPlugin().ConnectAsync(request, Context(), CancellationToken.None));
        Assert.Equal(ProtocolFailureKind.Configuration, error.Kind);
    }

    [Fact]
    public async Task RefusedConnectionIsNetworkFailure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var request = new ConnectionRequest { Host = "127.0.0.1", Port = port, Username = "", ConnectTimeout = TimeSpan.FromSeconds(5) };
        var error = await Assert.ThrowsAsync<ProtocolException>(() => new TelnetProtocolPlugin().ConnectAsync(request, Context(), CancellationToken.None));
        Assert.Equal(ProtocolFailureKind.Network, error.Kind);
    }

    [Fact]
    public async Task PreCancelledConnectDoesNotOpenSocket()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TelnetProtocolPlugin().ConnectAsync(
            new ConnectionRequest { Host = "127.0.0.1", Username = "" }, Context(), cancellation.Token));
    }

    [Fact]
    public void DescriptorUsesPort23AndOnlyTerminalCapability()
    {
        var descriptor = new TelnetProtocolPlugin().Descriptor;
        Assert.Equal("telnet", descriptor.Id);
        Assert.Equal(23, descriptor.DefaultPort);
        Assert.Equal(SessionCapabilities.Terminal, descriptor.Capabilities);
        Assert.True(descriptor.AuthenticatesInTerminal);
    }

    private static Task<IProtocolSession> ConnectAsync(TcpListener listener, CancellationToken token) =>
        new TelnetProtocolPlugin().ConnectAsync(new ConnectionRequest
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            Username = "", KeepAliveInterval = TimeSpan.Zero,
        }, Context(), token);

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length, CancellationToken token)
    {
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private static ProtocolHostContext Context() => new(NullLoggerFactory.Instance, new UnusedHostKeyStore());

    private sealed class UnusedHostKeyStore : IHostKeyStore
    {
        public bool TryGetFingerprint(string endpoint, out KnownHostEntry entry) => throw new InvalidOperationException("Telnet has no host keys.");
        public void Save(KnownHostEntry entry) => throw new InvalidOperationException("Telnet has no host keys.");
        public bool Remove(string endpoint) => throw new InvalidOperationException("Telnet has no host keys.");
        public IReadOnlyList<KnownHostEntry> List() => throw new InvalidOperationException("Telnet has no host keys.");
    }
}
