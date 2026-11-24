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
        Assert.Equal(32768, options.ShellBufferSize);
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
    [InlineData("not-a-number", 32768)]
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

    [Fact]
    public void DescriptorExposesTheAdvancedOptionsInOrder()
    {
        var fields = SshProtocolPlugin.DefaultDescriptor.ConnectionOptions;

        Assert.Same(SshProtocolPlugin.AdvancedOptions, fields);
        Assert.Equal(
            new[]
            {
                SshProtocolOptions.EncodingKey,
                SshProtocolOptions.TerminalTypeKey,
                SshProtocolOptions.UseLegacyAlgorithmsKey,
                SshProtocolOptions.PreferredKeyExchangeKey,
                SshProtocolOptions.CommandTimeoutSecondsKey,
                SshProtocolOptions.ProxyJumpKey,
                SshProtocolOptions.ProxyJumpPrivateKeyKey,
                SshProtocolOptions.PortForwardsKey,
            },
            fields.Select(field => field.Key));
    }

    [Fact]
    public void DescriptorDefaultsAreValid()
    {
        var fields = SshProtocolPlugin.DefaultDescriptor.ConnectionOptions.ToDictionary(field => field.Key, StringComparer.Ordinal);

        Assert.Equal(8, fields.Count);
        Assert.Equal("utf-8", fields[SshProtocolOptions.EncodingKey].DefaultValue);
        Assert.Equal(SshProtocolOptions.DefaultTerminalType, fields[SshProtocolOptions.TerminalTypeKey].DefaultValue);
        Assert.Equal("false", fields[SshProtocolOptions.UseLegacyAlgorithmsKey].DefaultValue);
        Assert.Equal(string.Empty, fields[SshProtocolOptions.PreferredKeyExchangeKey].DefaultValue);
        Assert.Equal("20", fields[SshProtocolOptions.CommandTimeoutSecondsKey].DefaultValue);
        Assert.Empty(fields[SshProtocolOptions.ProxyJumpKey].DefaultValue);
        Assert.Empty(fields[SshProtocolOptions.ProxyJumpPrivateKeyKey].DefaultValue);
        Assert.Empty(fields[SshProtocolOptions.PortForwardsKey].DefaultValue);
        Assert.All(fields.Values, field => Assert.True(field.IsValid(field.DefaultValue), $"Default value of {field.Key} must be valid."));
    }

    [Fact]
    public void DescriptorValidationRejectsUnknownChoicesAndOutOfRangeValues()
    {
        var fields = SshProtocolPlugin.DefaultDescriptor.ConnectionOptions.ToDictionary(field => field.Key, StringComparer.Ordinal);

        var encoding = fields[SshProtocolOptions.EncodingKey];
        Assert.Equal(new[] { "utf-8", "latin1", "ascii" }, encoding.Choices);
        Assert.True(encoding.IsValid("latin1"));
        Assert.True(encoding.IsValid("UTF-8"));
        Assert.False(encoding.IsValid("utf-16"));

        Assert.True(fields[SshProtocolOptions.TerminalTypeKey].IsValid("screen"));
        Assert.True(fields[SshProtocolOptions.TerminalTypeKey].IsValid(string.Empty));

        var legacy = fields[SshProtocolOptions.UseLegacyAlgorithmsKey];
        Assert.True(legacy.IsBoolean);
        Assert.True(legacy.IsValid("true"));
        Assert.False(legacy.IsValid("maybe"));

        var timeout = fields[SshProtocolOptions.CommandTimeoutSecondsKey];
        Assert.Equal(1, timeout.Minimum);
        Assert.Equal(300, timeout.Maximum);
        Assert.True(timeout.IsValid("1"));
        Assert.True(timeout.IsValid("300"));
        Assert.False(timeout.IsValid("0"));
        Assert.False(timeout.IsValid("301"));
        Assert.False(timeout.IsValid("20.5"));
    }

    [Fact]
    public void NonDefaultOptionsAreReadFromTheRequest()
    {
        var options = new SshProtocolOptions(CreateRequest(new Dictionary<string, string>
        {
            [SshProtocolOptions.EncodingKey] = "latin1",
            [SshProtocolOptions.TerminalTypeKey] = "screen",
            [SshProtocolOptions.UseLegacyAlgorithmsKey] = "true",
            [SshProtocolOptions.PreferredKeyExchangeKey] = "curve25519-sha256",
            [SshProtocolOptions.CommandTimeoutSecondsKey] = "60",
        }));

        Assert.Equal("latin1", options.EncodingName);
        Assert.Equal(Encoding.Latin1, options.Encoding);
        Assert.Equal("screen", options.TerminalType);
        Assert.True(options.UseLegacyAlgorithms);
        Assert.Equal("curve25519-sha256", options.PreferredKeyExchange);
        Assert.Equal(TimeSpan.FromSeconds(60), options.CommandTimeout);
    }

    private static ConnectionRequest CreateRequest(IReadOnlyDictionary<string, string>? options = null) => new()
    {
        Host = "example.com",
        Port = 2222,
        Username = "alice",
        Options = options ?? new Dictionary<string, string>(),
    };
}
