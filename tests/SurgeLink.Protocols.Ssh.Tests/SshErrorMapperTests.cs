using System.IO;
using System.Net.Sockets;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using SurgeLink.Core.Protocols;
using SurgeLink.Protocols.Ssh;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class SshErrorMapperTests
{
    [Fact]
    public void AuthenticationFailuresMapToAuthentication()
    {
        var exception = SshErrorMapper.Map(new SshAuthenticationException("Permission denied"), "connect to host:22");

        Assert.Equal(ProtocolFailureKind.Authentication, exception.Kind);
        Assert.Contains("authentication failed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("connect to host:22", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionFailuresMapToNetwork()
    {
        var exception = SshErrorMapper.Map(new SshConnectionException("socket reset"), "open the remote shell");

        Assert.Equal(ProtocolFailureKind.Network, exception.Kind);
    }

    [Fact]
    public void SocketAndIoFailuresMapToNetwork()
    {
        Assert.Equal(ProtocolFailureKind.Network, SshErrorMapper.Classify(new SocketException(10061)));
        Assert.Equal(ProtocolFailureKind.Network, SshErrorMapper.Classify(new IOException("broken pipe")));
    }

    [Fact]
    public void TimeoutsMapToTimeout()
    {
        Assert.Equal(ProtocolFailureKind.Timeout, SshErrorMapper.Classify(new SshOperationTimeoutException("late")));
        Assert.Equal(ProtocolFailureKind.Timeout, SshErrorMapper.Classify(new TimeoutException("late")));
    }

    [Fact]
    public void CancellationMapsToCancelled()
    {
        var exception = SshErrorMapper.Map(new OperationCanceledException(), "connect to host:22");

        Assert.Equal(ProtocolFailureKind.Cancelled, exception.Kind);
        Assert.Contains("cancelled", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidArgumentsMapToConfiguration()
    {
        Assert.Equal(ProtocolFailureKind.Configuration, SshErrorMapper.Classify(new ArgumentException("bad")));
    }

    [Fact]
    public void ProtocolExceptionsAreReturnedUnchanged()
    {
        var original = new ProtocolException("already mapped", ProtocolFailureKind.HostKey);

        Assert.Same(original, SshErrorMapper.Map(original, "connect"));
        Assert.Same(original, SshErrorMapper.MapFileSystem(original, "/tmp/x", "read"));
    }

    [Fact]
    public void UnknownExceptionsMapToUnknown()
    {
        var exception = SshErrorMapper.Map(new InvalidOperationException("boom"), "sample");

        Assert.Equal(ProtocolFailureKind.Unknown, exception.Kind);
        Assert.Contains("boom", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullExceptionIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => SshErrorMapper.Map(null!, "connect"));
        Assert.Throws<ArgumentNullException>(() => SshErrorMapper.Classify(null!));
    }

    [Fact]
    public void PermissionDeniedNamesThePath()
    {
        var exception = SshErrorMapper.MapFileSystem(
            new SftpPermissionDeniedException("denied"),
            "/srv/secret.txt",
            "open for reading");

        Assert.Equal(ProtocolFailureKind.ServerRejected, exception.Kind);
        Assert.Contains("Permission denied", exception.Message, StringComparison.Ordinal);
        Assert.Contains("/srv/secret.txt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPathMapsToConfiguration()
    {
        var exception = SshErrorMapper.MapFileSystem(
            new SftpPathNotFoundException("No such file", "/srv/gone.txt"),
            "/srv/gone.txt",
            "list directory");

        Assert.Equal(ProtocolFailureKind.Configuration, exception.Kind);
        Assert.Contains("not found", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/srv/gone.txt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelledFileOperationsAreMapped()
    {
        var exception = SshErrorMapper.MapFileSystem(new OperationCanceledException(), "/srv/file", "delete");

        Assert.Equal(ProtocolFailureKind.Cancelled, exception.Kind);
        Assert.Contains("cancelled", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OtherFileOperationFailuresMentionOperationAndPath()
    {
        var exception = SshErrorMapper.MapFileSystem(new SshException("channel closed"), "/srv/file", "rename to '/srv/other'");

        Assert.Contains("rename to '/srv/other'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("/srv/file", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FileSystemMappingValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => SshErrorMapper.MapFileSystem(null!, "/x", "read"));
        Assert.Throws<ArgumentNullException>(() => SshErrorMapper.MapFileSystem(new IOException(), null!, "read"));
        Assert.Throws<ArgumentNullException>(() => SshErrorMapper.MapFileSystem(new IOException(), "/x", null!));
    }

    [Fact]
    public void SftpFailuresWithoutSpecificTypeMapToServerRejected()
    {
        Assert.Equal(
            ProtocolFailureKind.ServerRejected,
            SshErrorMapper.Classify(new SftpException(StatusCode.Failure, "no space left")));
    }
}