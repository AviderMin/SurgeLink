using SurgeLink.App.Sessions;
using SurgeLink.Core.Files;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Tests.Fakes;

namespace SurgeLink.Core.Tests;

public sealed class ConnectedServerCountTests
{
    [Fact]
    public void DuplicateSessionsCountAsOneServer() =>
        Assert.Equal(2, Snapshot(Tab("192.168.8.1"), Tab("192.168.8.110"), Tab("192.168.8.110")).ConnectedServerCount);

    [Fact]
    public void UsersAndPortsDoNotCreateExtraServers() =>
        Assert.Equal(1, Snapshot(Tab("server.example", user: "root", port: 22),
            Tab("SERVER.EXAMPLE.", user: "ubuntu", port: 2233)).ConnectedServerCount);

    [Fact]
    public void EquivalentIpv6AddressesCountAsOneServer() =>
        Assert.Equal(1, Snapshot(Tab("[2001:db8::1]"), Tab("2001:0db8:0:0:0:0:0:1")).ConnectedServerCount);

    [Fact]
    public void DisconnectedAndDemoSessionsAreNotServers() =>
        Assert.Equal(0, Snapshot(Tab("server", state: ConnectionState.Disconnected),
            Tab("other", state: ConnectionState.Faulted), Tab("demo-host", protocol: "demo")).ConnectedServerCount);

    [Fact]
    public void EmptyWorkspaceHasNoServers() => Assert.Equal(0, WorkspaceSnapshot.Empty.ConnectedServerCount);

    private static WorkspaceSnapshot Snapshot(params WorkspaceTab[] tabs) => new([], tabs, null, "", false);

    private static WorkspaceTab Tab(string host, string user = "root", int port = 22,
        string protocol = "ssh", ConnectionState state = ConnectionState.Connected)
    {
        var profile = SessionProfile.New(protocol, host, port, user, host);
        var session = new FakeProtocolSession(new FakeProtocolPlugin(protocol).Descriptor);
        return new WorkspaceTab(Guid.NewGuid(), profile, session, null, new FileTransferQueue()) { State = state };
    }
}
