using System.Net;
using System.Net.Sockets;
using SurgeLink.Protocols.Ssh.Internal;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class LoopbackConnectionOwnerTests
{
    [WindowsLoopbackFact]
    public async Task IdentifiesTheClientProcessAndRequiresBothPorts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var listenerPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient(AddressFamily.InterNetwork);
        var accept = listener.AcceptTcpClientAsync(timeout.Token);
        await client.ConnectAsync(IPAddress.Loopback, listenerPort, timeout.Token);
        using var accepted = await accept;
        var clientPort = ((IPEndPoint)accepted.Client.RemoteEndPoint!).Port;

        Assert.True(LoopbackConnectionOwner.IsOwnedBy((uint)clientPort, (uint)listenerPort, Environment.ProcessId));
        Assert.False(LoopbackConnectionOwner.IsOwnedBy((uint)clientPort, (uint)listenerPort, Environment.ProcessId + 1));
        Assert.False(LoopbackConnectionOwner.IsOwnedBy((uint)clientPort, 0, Environment.ProcessId));
    }

    private sealed class WindowsLoopbackFactAttribute : FactAttribute
    {
        public WindowsLoopbackFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) { Skip = "Requires the Windows TCP owner table."; }
        }
    }
}
