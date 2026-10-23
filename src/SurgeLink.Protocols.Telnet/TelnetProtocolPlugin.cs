using System.Net.Sockets;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;

namespace SurgeLink.Protocols.Telnet;

/// <summary>Interactive Telnet over TCP; login takes place in the terminal.</summary>
public sealed class TelnetProtocolPlugin : IProtocolPlugin
{
    public const int DefaultPort = 23;

    public static ProtocolDescriptor DefaultDescriptor { get; } = new(
        "telnet", "Telnet", DefaultPort, SessionCapabilities.Terminal)
    {
        Glyph = "\uE774",
        Description = "Telnet terminal over TCP (unencrypted)",
        AuthenticatesInTerminal = true,
    };

    public ProtocolDescriptor Descriptor => DefaultDescriptor;

    public async Task<IProtocolSession> ConnectAsync(
        ConnectionRequest request, ProtocolHostContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        int port = request.Port == 0 ? DefaultPort : request.Port;
        if (string.IsNullOrWhiteSpace(request.Host) || port is < 1 or > 65535 ||
            request.ConnectTimeout <= TimeSpan.Zero || request.ConnectTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ProtocolException("Telnet requires a host, a valid port and a positive connection timeout.",
                ProtocolFailureKind.Configuration);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.ConnectTimeout);
        var client = new TcpClient { NoDelay = true };
        try
        {
            if (request.KeepAliveInterval > TimeSpan.Zero)
            {
                int seconds = (int)Math.Clamp(Math.Ceiling(request.KeepAliveInterval.TotalSeconds), 1, 3600);
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, seconds);
                client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, seconds);
            }

            await client.ConnectAsync(request.Host, port, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return new TelnetSession(client, request.Host, context.CreateLogger<TelnetSession>());
        }
        catch (OperationCanceledException exception)
        {
            client.Dispose();
            throw new ProtocolException(cancellationToken.IsCancellationRequested
                    ? "The Telnet connection was cancelled." : "The Telnet connection timed out.",
                cancellationToken.IsCancellationRequested ? ProtocolFailureKind.Cancelled : ProtocolFailureKind.Timeout,
                exception);
        }
        catch (Exception exception) when (exception is SocketException or IOException or ArgumentException)
        {
            client.Dispose();
            throw new ProtocolException($"Could not connect to Telnet server {request.Host}:{port}: {exception.Message}",
                exception is ArgumentException ? ProtocolFailureKind.Configuration : ProtocolFailureKind.Network, exception);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
