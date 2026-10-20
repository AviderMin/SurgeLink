using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;

namespace SurgeLink.Core.Tests;

public sealed class ModelTests
{
    [Fact]
    public void NewProfileGeneratesIdAndDefaultName()
    {
        SessionProfile profile = SessionProfile.New("ssh", "example.test", 2222, "alice");

        Assert.False(string.IsNullOrWhiteSpace(profile.Id));
        Assert.Equal("alice@example.test", profile.Name);
        Assert.Equal("ssh", profile.ProtocolId);
        Assert.Equal("example.test", profile.Host);
        Assert.Equal(2222, profile.Port);
        Assert.Equal("alice", profile.Username);
        Assert.Equal(AuthenticationMethod.Password, profile.Authentication);
        Assert.Empty(profile.Options);
    }

    [Fact]
    public void NewProfileUsesExplicitName()
    {
        SessionProfile profile = SessionProfile.New("ssh", "example.test", 0, "alice", "Production box");

        Assert.Equal("Production box", profile.Name);
    }

    [Fact]
    public void NewProfilesGetUniqueIds()
    {
        SessionProfile first = SessionProfile.New("ssh", "a", 22, "u");
        SessionProfile second = SessionProfile.New("ssh", "a", 22, "u");

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void MarkUsedSetsTimestamp()
    {
        SessionProfile profile = SessionProfile.New("ssh", "example.test", 22, "alice");
        var now = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.FromHours(2));

        SessionProfile used = profile.MarkUsed(now);

        Assert.Equal(now, used.LastUsedUtc);
        Assert.Null(profile.LastUsedUtc);
    }

    [Theory]
    [InlineData(0, 22, 22)]
    [InlineData(2200, 22, 2200)]
    [InlineData(-1, 2222, 2222)]
    public void ResolvePortFallsBackToProtocolDefault(int port, int defaultPort, int expected)
    {
        SessionProfile profile = SessionProfile.New("ssh", "example.test", port, "alice");

        Assert.Equal(expected, profile.ResolvePort(defaultPort));
    }

    [Fact]
    public void ConnectionRequestGetOptionReturnsStoredValue()
    {
        var request = new ConnectionRequest
        {
            Host = "example.test",
            Username = "alice",
            Options = new Dictionary<string, string> { ["encoding"] = "utf-8" },
        };

        Assert.Equal("utf-8", request.GetOption("encoding", "ascii"));
        Assert.Equal("ascii", request.GetOption("missing", "ascii"));
        Assert.Equal("ascii", request.GetOption("missing", "ascii"));
    }

    [Fact]
    public void ConnectionRequestGetOptionTreatsEmptyAsMissing()
    {
        var request = new ConnectionRequest
        {
            Host = "example.test",
            Username = "alice",
            Options = new Dictionary<string, string> { ["encoding"] = string.Empty },
        };

        Assert.Equal("fallback", request.GetOption("encoding", "fallback"));
    }

    [Theory]
    [InlineData("true", false, true)]
    [InlineData("false", true, false)]
    [InlineData("garbage", true, true)]
    public void ConnectionRequestGetFlagParsesBooleanOptions(string value, bool fallback, bool expected)
    {
        var request = new ConnectionRequest
        {
            Host = "example.test",
            Username = "alice",
            Options = new Dictionary<string, string> { ["agent"] = value },
        };

        Assert.Equal(expected, request.GetFlag("agent", fallback));
    }

    [Fact]
    public void ConnectionRequestGetFlagUsesFallbackWhenAbsent()
    {
        var request = new ConnectionRequest { Host = "example.test", Username = "alice" };

        Assert.False(request.GetFlag("agent"));
        Assert.True(request.GetFlag("agent", fallback: true));
    }

    [Fact]
    public void RemoteHostInfoSummaryCombinesKnownParts()
    {
        var info = new RemoteHostInfo
        {
            HostName = "srv1",
            OperatingSystem = "Ubuntu 24.04",
            Architecture = "x86_64",
        };

        Assert.Equal("srv1 · Ubuntu 24.04 · x86_64", info.Summary);
    }

    [Fact]
    public void RemoteHostInfoSummaryHandlesEmptyInfo()
    {
        Assert.Equal("Unknown host", RemoteHostInfo.Empty.Summary);
        Assert.Equal("Unknown host", new RemoteHostInfo { HostName = "  " }.Summary);
    }
}
