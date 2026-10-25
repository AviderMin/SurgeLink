using Microsoft.Extensions.Logging;
using Renci.SshNet;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;
using SurgeLink.Protocols.Ssh.Internal;

namespace SurgeLink.Protocols.Ssh;

/// <summary>
/// SSH protocol plugin: interactive shell, SFTP/SCP file system and Linux monitoring
/// within one workspace session. File capabilities authenticate separate transports.
/// </summary>
public sealed class SshProtocolPlugin : IProtocolPlugin
{
    /// <summary>Default TCP port for SSH.</summary>
    public const int DefaultPort = 22;

    /// <summary>Metadata used by the session picker and the composition root.</summary>
    public static ProtocolDescriptor DefaultDescriptor { get; } = new(
        "ssh",
        "SSH",
        DefaultPort,
        SessionCapabilities.Terminal | SessionCapabilities.FileSystem | SessionCapabilities.Monitor)
    {
        Glyph = "\uE8CE",
        Description = "Secure Shell, SFTP/SCP and remote monitor",
    };

    /// <inheritdoc />
    public ProtocolDescriptor Descriptor => DefaultDescriptor;

    /// <inheritdoc />
    public async Task<IProtocolSession> ConnectAsync(
        ConnectionRequest request,
        ProtocolHostContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var logger = context.CreateLogger<SshSession>();
        var options = new SshProtocolOptions(request);
        var factory = new SshConnectionFactory(request, options, context, logger);
        using var timeout = factory.CreateConnectBudget(cancellationToken);

        ConnectionInfo connectionInfo;
        HostKeyVerifier verifier;
        try
        {
            connectionInfo = factory.CreateConnectionInfo(timeout.Token, timeout);
            verifier = factory.CreateVerifier(timeout.Token, timeout);
        }
        catch (ProtocolException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw SshErrorMapper.Map(exception, $"prepare a connection to {factory.Endpoint}");
        }

        var client = new SshClient(connectionInfo);
        client.HostKeyReceived += verifier.Handle;

        try
        {
            if (request.KeepAliveInterval > TimeSpan.Zero)
            {
                client.KeepAliveInterval = ClampKeepAlive(request.KeepAliveInterval);
            }

            await timeout.ConnectAsync(client).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            client.HostKeyReceived -= verifier.Handle;
            client.Dispose();
            if (cancellationToken.IsCancellationRequested)
            {
                throw new ProtocolException(
                    $"Connecting to {factory.Endpoint} was cancelled.",
                    ProtocolFailureKind.Cancelled,
                    exception);
            }

            throw new ProtocolException(
                $"Connecting to {factory.Endpoint} timed out after {options.ConnectTimeout.TotalSeconds:0} seconds.",
                ProtocolFailureKind.Timeout,
                exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            client.HostKeyReceived -= verifier.Handle;
            client.Dispose();
            if (cancellationToken.IsCancellationRequested)
            {
                throw new ProtocolException(
                    $"Connecting to {factory.Endpoint} was cancelled.",
                    ProtocolFailureKind.Cancelled,
                    exception);
            }

            if (verifier.RejectionReason is { Length: > 0 } reason)
            {
                if (timeout.IsCancellationRequested)
                    throw new ProtocolException($"Connecting to {factory.Endpoint} timed out.", ProtocolFailureKind.Timeout, exception);
                throw new ProtocolException(reason, ProtocolFailureKind.HostKey, exception);
            }

            throw SshErrorMapper.Map(exception, $"connect to {factory.Endpoint}");
        }

        try
        {
            return new SshSession(client, factory, options, Descriptor, logger);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static TimeSpan ClampKeepAlive(TimeSpan interval) =>
        interval > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : interval;
}
