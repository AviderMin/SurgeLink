using System.Net.Sockets;
using Renci.SshNet.Common;
using SurgeLink.Core.Protocols;

namespace SurgeLink.Protocols.Ssh;

/// <summary>
/// Translates SSH.NET exceptions into <see cref="ProtocolException"/> instances with a
/// user presentable message and a failure kind the UI can act on.
/// </summary>
public static class SshErrorMapper
{
    /// <summary>Maps an arbitrary exception raised by a connection or command.</summary>
    /// <param name="exception">Exception to map.</param>
    /// <param name="context">Short description of what the code was doing, for example <c>connect to host:22</c>.</param>
    public static ProtocolException Map(Exception exception, string context)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is ProtocolException protocolException)
        {
            return protocolException;
        }

        var kind = Classify(exception);
        var description = Describe(exception);
        var doing = string.IsNullOrWhiteSpace(context) ? "during an SSH operation" : context;
        var message = kind switch
        {
            ProtocolFailureKind.Authentication => $"SSH authentication failed while trying to {doing}. {description}",
            ProtocolFailureKind.Timeout => $"The SSH operation timed out while trying to {doing}. {description}",
            ProtocolFailureKind.Network => $"The SSH connection was lost while trying to {doing}. {description}",
            ProtocolFailureKind.Configuration => $"The SSH request is not valid while trying to {doing}. {description}",
            ProtocolFailureKind.Cancelled => $"The SSH operation was cancelled while trying to {doing}.",
            ProtocolFailureKind.ServerRejected => $"The SSH server rejected the request while trying to {doing}. {description}",
            _ => $"SSH failed while trying to {doing}. {description}",
        };
        return new ProtocolException(message, kind, exception);
    }

    /// <summary>Maps an exception raised by a file system operation and names the affected path.</summary>
    /// <param name="exception">Exception to map.</param>
    /// <param name="path">Remote path the operation was working on.</param>
    /// <param name="operation">Short verb phrase, for example <c>list directory</c>.</param>
    public static ProtocolException MapFileSystem(Exception exception, string path, string operation)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(operation);

        if (exception is ProtocolException protocolException)
        {
            return protocolException;
        }

        var kind = Classify(exception);
        var message = exception switch
        {
            SftpPermissionDeniedException => $"Permission denied when trying to {operation} '{path}'.",
            SftpPathNotFoundException => $"Remote path not found: '{path}'.",
            OperationCanceledException => $"The file system operation '{operation}' on '{path}' was cancelled.",
            _ => $"Could not {operation} '{path}' over SFTP. {Describe(exception)}",
        };
        return new ProtocolException(message, kind, exception);
    }

    /// <summary>Classifies an exception into a <see cref="ProtocolFailureKind"/>.</summary>
    /// <param name="exception">Exception to classify.</param>
    public static ProtocolFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            OperationCanceledException => ProtocolFailureKind.Cancelled,
            SshAuthenticationException => ProtocolFailureKind.Authentication,
            SshOperationTimeoutException or TimeoutException => ProtocolFailureKind.Timeout,
            SshConnectionException or SocketException => ProtocolFailureKind.Network,
            SftpPermissionDeniedException => ProtocolFailureKind.ServerRejected,
            SftpPathNotFoundException => ProtocolFailureKind.Configuration,
            ArgumentException => ProtocolFailureKind.Configuration,
            SftpException => ProtocolFailureKind.ServerRejected,
            SshException => ProtocolFailureKind.ServerRejected,
            IOException => ProtocolFailureKind.Network,
            _ => ProtocolFailureKind.Unknown,
        };
    }

    private static string Describe(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;
}
