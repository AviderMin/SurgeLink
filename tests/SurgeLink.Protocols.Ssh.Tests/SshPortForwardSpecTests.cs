using SurgeLink.Core.Protocols;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class SshPortForwardSpecTests
{
    [Fact]
    public void ParsesLocalRemoteAndDynamicForwards()
    {
        var specs = SshPortForwardSpec.ParseList("L:8080:127.0.0.1:80; R:0.0.0.0:9090:localhost:9000; D:1080");

        Assert.Equal(3, specs.Count);
        Assert.Equal(new SshPortForwardSpec(SshPortForwardKind.Local, "127.0.0.1", 8080, "127.0.0.1", 80), specs[0]);
        Assert.Equal(new SshPortForwardSpec(SshPortForwardKind.Remote, "0.0.0.0", 9090, "localhost", 9000), specs[1]);
        Assert.Equal(new SshPortForwardSpec(SshPortForwardKind.Dynamic, "127.0.0.1", 1080, null, 0), specs[2]);
    }

    [Theory]
    [InlineData("X:8080:localhost:80")]
    [InlineData("L:0:localhost:80")]
    [InlineData("L:8080:localhost:70000")]
    [InlineData("D:notaport")]
    public void InvalidPortForwardsThrowConfigurationProtocolException(string raw)
    {
        var ex = Assert.Throws<ProtocolException>(() => SshPortForwardSpec.ParseList(raw));
        Assert.Equal(ProtocolFailureKind.Configuration, ex.Kind);
    }

    [Theory]
    [InlineData("bastion.example.com", "alice", "bastion.example.com", 22, "alice")]
    [InlineData("ops@bastion.example.com:2222", "alice", "bastion.example.com", 2222, "ops")]
    [InlineData("ops@[::1]:2200", "alice", "::1", 2200, "ops")]
    public void ParsesJumpTargets(string raw, string defaultUser, string host, int port, string user)
    {
        var target = SshJumpTarget.Parse(raw, defaultUser);
        Assert.Equal(host, target.Host);
        Assert.Equal(port, target.Port);
        Assert.Equal(user, target.Username);
    }
}
