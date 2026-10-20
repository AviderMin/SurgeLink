using System.Collections.Generic;
using System.Text;
using SurgeLink.Core.Protocols;
using SurgeLink.Protocols.Ssh;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class SshProtocolOptionsTests
{
    [Fact]
    public void OptionsExposeSaneDefaults()
    {
        var options = new SshProtocolOptions(CreateRequest());

        Assert.Equal("xterm-256color", options.TerminalType);
        Assert.Equal("utf-8", options.EncodingName);
        Assert.Equal(Encoding.UTF8, options.Encoding);
        Assert.False(options.UseLegacyAlgorithms);
        Assert.Null(options.PreferredKeyExchange);
        Assert.Equal(4096, options.ShellBufferSize);
        Assert.Equal(TimeSpan.FromSeconds(20), options.CommandTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), options.ConnectTimeout);
    }

    [Theory]
    [InlineData("terminalType", "screen-256color", "screen-256color")]
    [InlineData("term", "vt100", "vt100")]
    public void TerminalTypeOptionIsRead(string key, string value, string expected)
    {
        var options = new SshProtocolOptions(CreateRequest(new Dictionary<string, string> { [key] = value }));

        Assert.Equal(expected, options.TerminalType);
    }

    [Fact]
    public void TerminalTypePrefersTheCamelCaseKey()
    {
        var options = new SshProtocolOptions(CreateRequest(new Dictionary<string, string>
        {
            ["terminalType"] = "screen",
            ["term"] = "vt100",
        }));

        Assert.Equal("screen", options.TerminalType);
    }

    [Theory]
    [InlineData("latin1", "iso-8859-1")]
    [InlineData("iso-8859-1", "iso-8859-1")]
    [InlineData("ascii", "us-ascii")]
    [InlineData("utf8", "utf-8")]
    [InlineData("no-such-encoding", "utf-8")]
    public void EncodingOptionIsResolvedWithAUtf8Fallback(string name, string expectedWebName)
    {
        var options = new SshProtocolOptions(CreateRequest(new Dictionary<string, string> { ["encoding"] = name }));

        Assert.Equal(expectedWebName, options.Encoding.WebName);
        Assert.Equal(name, options.EncodingName);
    }

    [Fact]
    public void ResolveEncodingHandlesNullAndEmpty()
    {
        Assert.Equal(Encoding.UTF8, SshProtocolOptions.ResolveEncoding(null));
        Assert.Equal(Encoding.UTF8, SshProtocolOptions.ResolveEncoding("  "));
    }

    [Theory]
    [InlineData("useLegacyAlgorithms", "true", true)]
    [InlineData("legacyAlgorithms", "True", true)]
    [InlineData("useLegacyAlgorithms", "false", false)]
    public void LegacyAlgorithmFlagIsRead(string key, string value, bool expected)
    {
        var options = new SshProtocolOptions(CreateRequest(new Dictionary<string, string> { [key] = value }));

        Assert.Equal(expected, options.UseLegacyAlgorithms);
    }

    [Theory]
    [InlineData("preferredKeyExchange", "curve25519-sha256", "curve25519-sha256")]
    [InlineData("kex", "diffie-hellman-group14-sha256", "diffie-hellman-group14-sha256")]
    public void PreferredKeyExchangeOptionIsRead(string key, string value, string expected)
    {
        var options = new SshProtocolOptions(CreateRequest(new Dictionary<string, string> { [key] = value }));

        Assert.Equal(expected, options.PreferredKeyExchange);
    }

    [Theory]
    [InlineData("100", 256)]
    [InlineData("8192", 8192)]
    [InlineData("1000000", 65536)]
    [InlineData("not-a-number", 4096)]
    public void ShellBufferSizeIsClamped(string value, int expected)
    {
        var options = new SshProtocolOptions(CreateRequest(new Dictionary<string, string> { ["shellBufferSize"] = value }));

        Assert.Equal(expected, options.ShellBufferSize);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("1000", 300)]
    [InlineData("-4", 20)]
    [InlineData("nope", 20)]
    public void CommandTimeoutIsClamped(string value, int expectedSeconds)
    {
        var options = new SshProtocolOptions(CreateRequest(new Dictionary<string, string> { ["commandTimeoutSeconds"] = value }));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.CommandTimeout);
    }

    [Fact]
    public void ConnectTimeoutIsClamped()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), new SshProtocolOptions(CreateRequest() with { ConnectTimeout = TimeSpan.Zero }).ConnectTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), new SshProtocolOptions(CreateRequest() with { ConnectTimeout = TimeSpan.FromHours(5) }).ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), new SshProtocolOptions(CreateRequest() with { ConnectTimeout = TimeSpan.FromSeconds(5) }).ConnectTimeout);
    }

    [Fact]
    public void ConstructorRejectsNullRequest()
    {
        Assert.Throws<ArgumentNullException>(() => new SshProtocolOptions(null!));
    }

    private static ConnectionRequest CreateRequest(IReadOnlyDictionary<string, string>? options = null) => new()
    {
        Host = "example.com",
        Port = 2222,
        Username = "alice",
        Options = options ?? new Dictionary<string, string>(),
    };
}
