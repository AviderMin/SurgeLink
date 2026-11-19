using System.Reflection;
using SurgeLink.Core.Files;
using SurgeLink.Core.Protocols;
using Renci.SshNet.Common;
using SurgeLink.Protocols.Ssh;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class SshFileSystemTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("///")]
    [InlineData("/.")]
    [InlineData("/..")]
    [InlineData("/dir/..")]
    public void RootPathProtectionCoversRepeatedAndDotSegments(string path)
    {
        Assert.True(SftpFileSystem.IsRootPath(path));
    }

    [Theory]
    [InlineData("/dir")]
    [InlineData("/dir/")]
    [InlineData("/dir//child")]
    [InlineData("/dir/../child")]
    public void RootPathProtectionDoesNotRejectNormalPaths(string path)
    {
        Assert.False(SftpFileSystem.IsRootPath(path));
    }

    [Fact]
    public void ForwardsTryStatToTheSelectedTransport()
    {
        var method = typeof(SshFileSystem).GetMethod(
            nameof(IRemoteFileSystem.TryStatAsync),
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [typeof(string), typeof(CancellationToken)],
            modifiers: null);

        Assert.NotNull(method);
        Assert.Equal(typeof(SshFileSystem), method!.DeclaringType);
    }

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
