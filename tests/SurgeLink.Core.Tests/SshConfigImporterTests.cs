using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;

namespace SurgeLink.Core.Tests;

public sealed class SshConfigImporterTests
{
    [Fact]
    public void ParsesHostsDefaultsJumpForwardsAndSkipsWildcards()
    {
        const string config = """
            # Global defaults
            Host *
                User defaultuser
                Port 2200
                IdentityFile ~/.ssh/id_ed25519

            Host prod-app
                HostName 10.0.1.25
                User deploy
                Port 2222
                ProxyJump bastion@jump.example.com:2202
                LocalForward 8080 127.0.0.1:80
                RemoteForward 9090 localhost:9000
                DynamicForward 1080

            Host *.internal !ignored
                User wildcard

            Host staging
                HostName staging.example.com
            """;

        var profiles = SshConfigImporter.Parse(config, @"C:\Users\alice");

        Assert.Equal(2, profiles.Count);

        var prod = profiles[0];
        Assert.Equal("prod-app", prod.Name);
        Assert.Equal("10.0.1.25", prod.Host);
        Assert.Equal(2222, prod.Port);
        Assert.Equal("deploy", prod.Username);
        Assert.Equal(AuthenticationMethod.PublicKey, prod.Authentication);
        Assert.Equal(Path.Combine(@"C:\Users\alice", ".ssh/id_ed25519"), prod.PrivateKeyPath);
        Assert.Equal("bastion@jump.example.com:2202", prod.Options["proxyJump"]);
        Assert.Equal("L:8080:127.0.0.1:80;R:9090:localhost:9000;D:1080", prod.Options["portForwards"]);
        Assert.Equal(SshConfigImporter.DefaultGroup, prod.Group);

        var staging = profiles[1];
        Assert.Equal("staging", staging.Name);
        Assert.Equal("staging.example.com", staging.Host);
        Assert.Equal("defaultuser", staging.Username);
        Assert.Equal(2200, staging.Port);
    }

    [Fact]
    public void ImportFromFileSkipsExistingDuplicates()
    {
        using var temp = new TempDirectory();
        var configPath = temp.File("config");
        File.WriteAllText(configPath, """
            Host web1
                HostName 10.0.0.1
                User ubuntu

            Host web2
                HostName 10.0.0.2
                User ubuntu
            """);

        var existing = new[]
        {
            SessionProfile.New("ssh", "10.0.0.1", 22, "ubuntu", "web1"),
        };

        var result = SshConfigImporter.ImportFromFile(configPath, existing, temp.Root);

        Assert.Single(result.Imported);
        Assert.Equal("web2", result.Imported[0].Name);
        Assert.Equal(1, result.SkippedDuplicates);
    }
}
