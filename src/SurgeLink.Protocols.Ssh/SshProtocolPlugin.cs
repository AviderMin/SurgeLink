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

    /// <summary>Protocol settings exposed by the session editor for SSH profiles.</summary>
    public static readonly IReadOnlyList<ProtocolOptionDescriptor> AdvancedOptions =
    [
        new(SshProtocolOptions.EncodingKey, "Command output encoding", "utf-8")
        {
            Choices = ["utf-8", "latin1", "ascii"],
            ChoiceLabels = new Dictionary<string, string>
            {
                ["utf-8"] = "UTF-8",
                ["latin1"] = "Latin-1 (ISO-8859-1)",
                ["ascii"] = "ASCII",
            },
        },
        new(SshProtocolOptions.TerminalTypeKey, "Terminal type", SshProtocolOptions.DefaultTerminalType),
        new(SshProtocolOptions.UseLegacyAlgorithmsKey, "Use legacy algorithms (weakens security)", "false")
        {
            IsBoolean = true,
        },
        new(SshProtocolOptions.PreferredKeyExchangeKey, "Preferred key exchange", string.Empty),
        new(SshProtocolOptions.CommandTimeoutSecondsKey, "Command timeout (seconds)", "20")
        {
            Minimum = 1,
            Maximum = 300,
        },
        new(SshProtocolOptions.ProxyJumpKey, "Jump host (user@host:port)", string.Empty),
        new(SshProtocolOptions.PortForwardsKey, "Port forwards (L/R/D, semicolon separated)", string.Empty),
    ];

    /// <summary>Metadata used by the session picker and the composition root.</summary>
    public static ProtocolDescriptor DefaultDescriptor { get; } = new(
        "ssh",
        "SSH",
        DefaultPort,
        SessionCapabilities.Terminal | SessionCapabilities.FileSystem | SessionCapabilities.Monitor)
    {
        Glyph = "\uE8CE",
        Description = "Secure Shell, SFTP/SCP and remote monitor",
        ConnectionOptions = AdvancedOptions,
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
        var forwardSpecs = SshPortForwardSpec.ParseList(options.PortForwardsRaw);
        var factory = new SshConnectionFactory(request, options, context, logger);
        using var timeout = factory.CreateConnectBudget(cancellationToken);

        SshClient? jumpClient = null;
        ForwardedPortLocal? jumpTunnel = null;
        if (!string.IsNullOrWhiteSpace(options.ProxyJump))
        {
            (jumpClient, jumpTunnel) = await OpenJumpTunnelAsync(
                request, options, context, logger, timeout, cancellationToken).ConfigureAwait(false);
            factory.SetJumpEndpoint("127.0.0.1", (int)jumpTunnel.BoundPort);
        }

        ConnectionInfo connectionInfo;
        HostKeyVerifier verifier;
        try
        {
            connectionInfo = factory.CreateConnectionInfo(timeout.Token, timeout);
            verifier = factory.CreateVerifier(timeout.Token, timeout);
        }
        catch (ProtocolException)
        {
            DisposeJump(jumpTunnel, jumpClient);
            throw;
        }
        catch (Exception exception)
        {
            DisposeJump(jumpTunnel, jumpClient);
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
            DisposeJump(jumpTunnel, jumpClient);
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
            DisposeJump(jumpTunnel, jumpClient);
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

        var startedForwards = new List<ForwardedPort>();
        try
        {
            foreach (var spec in forwardSpecs)
            {
                ForwardedPort port = spec.Kind switch
                {
                    SshPortForwardKind.Local => new ForwardedPortLocal(spec.BindAddress, spec.BindPort, spec.TargetHost!, spec.TargetPort),
                    SshPortForwardKind.Remote => new ForwardedPortRemote(spec.BindAddress, spec.BindPort, spec.TargetHost!, spec.TargetPort),
                    SshPortForwardKind.Dynamic => new ForwardedPortDynamic(spec.BindAddress, spec.BindPort),
                    _ => throw new ProtocolException("Unsupported port forwarding kind.", ProtocolFailureKind.Configuration),
                };
                client.AddForwardedPort(port);
                port.Start();
                startedForwards.Add(port);
            }

            return new SshSession(client, factory, options, Descriptor, logger, startedForwards, jumpTunnel, jumpClient);
        }
        catch (Exception ex)
        {
            foreach (var port in startedForwards)
            {
                try { port.Stop(); port.Dispose(); } catch { }
            }
            client.Dispose();
            DisposeJump(jumpTunnel, jumpClient);
            if (ex is ProtocolException) throw;
            throw SshErrorMapper.Map(ex, $"start port forwarding on {factory.Endpoint}");
        }
    }

    private static async Task<(SshClient Client, ForwardedPortLocal Tunnel)> OpenJumpTunnelAsync(
        ConnectionRequest targetRequest,
        SshProtocolOptions targetOptions,
        ProtocolHostContext context,
        ILogger<SshSession> logger,
        SshConnectBudget timeout,
        CancellationToken cancellationToken)
    {
        var jump = SshJumpTarget.Parse(targetOptions.ProxyJump!, targetRequest.Username);
        var jumpRequest = targetRequest with
        {
            Host = jump.Host,
            Port = jump.Port,
            Username = jump.Username,
        };
        var jumpFactory = new SshConnectionFactory(jumpRequest, targetOptions, context, logger);
        var jumpInfo = jumpFactory.CreateConnectionInfo(timeout.Token, timeout);
        var jumpVerifier = jumpFactory.CreateVerifier(timeout.Token, timeout);
        var jumpClient = new SshClient(jumpInfo);
        jumpClient.HostKeyReceived += jumpVerifier.Handle;
        ForwardedPortLocal? tunnel = null;
        try
        {
            if (targetRequest.KeepAliveInterval > TimeSpan.Zero)
            {
                jumpClient.KeepAliveInterval = ClampKeepAlive(targetRequest.KeepAliveInterval);
            }

            await timeout.ConnectAsync(jumpClient).ConfigureAwait(false);
            var targetPort = (uint)(targetRequest.Port > 0 ? targetRequest.Port : DefaultPort);
            tunnel = new ForwardedPortLocal("127.0.0.1", 0, targetRequest.Host, targetPort);
            jumpClient.AddForwardedPort(tunnel);
            tunnel.Start();
            return (jumpClient, tunnel);
        }
        catch (Exception ex)
        {
            jumpClient.HostKeyReceived -= jumpVerifier.Handle;
            DisposeJump(tunnel, jumpClient);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new ProtocolException($"Connecting to jump host {jumpFactory.Endpoint} was cancelled.", ProtocolFailureKind.Cancelled, ex);
            }
            if (jumpVerifier.RejectionReason is { Length: > 0 } reason)
            {
                throw new ProtocolException(reason, ProtocolFailureKind.HostKey, ex);
            }
            if (ex is ProtocolException) throw;
            throw SshErrorMapper.Map(ex, $"connect to jump host {jumpFactory.Endpoint}");
        }
    }

    private static void DisposeJump(ForwardedPortLocal? tunnel, SshClient? client)
    {
        if (tunnel is not null)
        {
            try { tunnel.Stop(); } catch { }
            try { tunnel.Dispose(); } catch { }
        }
        if (client is not null)
        {
            try { if (client.IsConnected) client.Disconnect(); } catch { }
            try { client.Dispose(); } catch { }
        }
    }

    private static TimeSpan ClampKeepAlive(TimeSpan interval) =>
        interval > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : interval;
}
