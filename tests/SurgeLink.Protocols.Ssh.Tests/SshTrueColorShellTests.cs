using SurgeLink.Core.Terminal;
using SurgeLink.Protocols.Ssh.Internal;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class SshTrueColorShellTests
{
    [Fact]
    public void PinnedSshNetProvidesThePreShellEnvironmentRequest()
    {
        Assert.True(SshTrueColorShell.IsAvailable);
    }

    [Theory]
    [InlineData("xterm-256color", true)]
    [InlineData("XTERM-256COLOR", true)]
    [InlineData("xterm-direct", true)]
    [InlineData("screen-256color", true)]
    [InlineData("vt100", false)]
    [InlineData("dumb", false)]
    public void OnlyModernTerminalTypesAdvertiseTrueColor(string terminalType, bool expected)
    {
        Assert.Equal(expected, TerminalStartOptions.AdvertisesTrueColor(terminalType));
    }
}
