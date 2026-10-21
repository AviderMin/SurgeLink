using SurgeLink.Core.Protocols;
using SurgeLink.Protocols.Ssh;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class SshFileSystemTests
{
    [Fact]
    public void DetectsSftpSubsystemFailure()
    {
        var exception = new ProtocolException(
            "SSH server rejected the request while trying to connect SFTP to example:22. Channel was closed.",
            ProtocolFailureKind.ServerRejected,
            new InvalidOperationException("Channel was closed."));

        Assert.True(SshFileSystem.IsSftpUnavailable(exception));
    }

    [Fact]
    public void DoesNotFallbackForAuthenticationFailure()
    {
        var exception = new ProtocolException(
            "SSH authentication failed while trying to connect SFTP to example:22.",
            ProtocolFailureKind.Authentication,
            new InvalidOperationException("Permission denied."));

        Assert.False(SshFileSystem.IsSftpUnavailable(exception));
    }
}
