using SurgeLink.Core.Protocols;
using Renci.SshNet.Common;
using SurgeLink.Protocols.Ssh;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class SshFileSystemTests
{
    [Fact]
    public void DetectsSftpSubsystemFailure()
    {
        var exception = new SshException("Subsystem 'sftp' could not be executed.");

        Assert.True(SshFileSystem.IsSftpUnavailable(exception));
    }

    [Fact]
    public void DoesNotFallbackForPeerControlledOperationOrDisconnectMessages()
    {
        Assert.False(SshFileSystem.IsSftpUnavailable(new SftpException(Renci.SshNet.Sftp.StatusCode.Failure, "sftp subsystem channel was closed")));
        Assert.False(SshFileSystem.IsSftpUnavailable(new ProtocolException("sftp subsystem failed", ProtocolFailureKind.ServerRejected,
            new SshException("Channel was closed."))));
        Assert.False(SshFileSystem.IsSftpUnavailable(new SshConnectionException("Channel was closed.")));
        Assert.True(SshFileSystem.IsSftpUnavailable(new SshException("Channel was closed.")));
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
