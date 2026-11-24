using SurgeLink.Core.Protocols;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class SshPortForwardSpecTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("bastion.key")]
    public async Task JumpHostUsesOnlyItsOwnCredentials(string? keyPath)
    {
        var target = new ConnectionRequest
        {
            Host = "target.test",
            Username = "target-user",
            Password = "target-password",
            PrivateKeyPath = "target.key",
            PrivateKeyPassphrase = "target-passphrase",
            ProfileId = "target-profile",
            Options = keyPath is null ? new Dictionary<string, string>() : new Dictionary<string, string>
            {
                [SshProtocolOptions.ProxyJumpPrivateKeyKey] = keyPath,
            },
        };
        var jump = await SshProtocolPlugin.CreateJumpRequestAsync(target, "jump-user@bastion.test:2222", (prompt, _) =>
        {
            Assert.True(prompt.IsSecret);
            Assert.Contains("jump-user@bastion.test:2222", prompt.Question);
            return Task.FromResult<string?>("jump-secret");
        }, CancellationToken.None);

        Assert.Equal("bastion.test", jump.Host);
        Assert.Equal(2222, jump.Port);
        Assert.Equal("jump-user", jump.Username);
        Assert.Equal(keyPath is null ? AuthenticationMethod.Password : AuthenticationMethod.PublicKey, jump.Authentication);
        Assert.Equal(keyPath is null ? "jump-secret" : null, jump.Password);
        Assert.Equal(keyPath, jump.PrivateKeyPath);
        Assert.Equal(keyPath is null ? null : "jump-secret", jump.PrivateKeyPassphrase);
        Assert.Null(jump.ProfileId);
    }

    [Fact]
    public async Task JumpHostCannotFallBackToTheTargetPasswordWhenNoPromptIsAvailable()
    {
        var target = new ConnectionRequest { Host = "target.test", Username = "user", Password = "target-password" };
        var error = await Assert.ThrowsAsync<ProtocolException>(() =>
            SshProtocolPlugin.CreateJumpRequestAsync(target, "bastion.test", null, CancellationToken.None));
        Assert.Equal(ProtocolFailureKind.Configuration, error.Kind);
    }

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
