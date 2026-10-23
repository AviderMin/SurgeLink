using System.IO.Ports;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Sessions;

namespace SurgeLink.Protocols.Serial;

/// <summary>Local COM-port terminal. Credentials are entered directly on the device console.</summary>
public sealed class SerialProtocolPlugin : IProtocolPlugin
{
    private readonly Func<SerialOptions, ISerialConnection> _createConnection;

    public SerialProtocolPlugin() : this(options => new SerialPortConnection(options)) { }
    internal SerialProtocolPlugin(Func<SerialOptions, ISerialConnection> createConnection) => _createConnection = createConnection;

    public static ProtocolDescriptor DefaultDescriptor { get; } = new("serial", "Serial", 0, SessionCapabilities.Terminal)
    {
        Glyph = "\uE88E",
        Description = "Local serial console",
        AuthenticatesInTerminal = true,
        UsesNetworkAddress = false,
        EndpointLabel = "Serial port",
        EndpointLabelKey = "protocol.serial.endpoint",
        DiscoverEndpoints = DiscoverPorts,
        ConnectionOptions = SerialOptions.Fields,
    };

    public ProtocolDescriptor Descriptor => DefaultDescriptor;

    public async Task<IProtocolSession> ConnectAsync(ConnectionRequest request, ProtocolHostContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var options = SerialOptions.Parse(request);
        if (request.ConnectTimeout <= TimeSpan.Zero || request.ConnectTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ProtocolException("Serial connection timeout must be positive.", ProtocolFailureKind.Configuration);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.ConnectTimeout);
        var opening = Task.Run(() =>
        {
            var connection = _createConnection(options);
            try { connection.Open(); return connection; }
            catch { connection.Dispose(); throw; }
        }, CancellationToken.None);
        try
        {
            var connection = await opening.WaitAsync(timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return new SerialSession(connection, options.PortName, context.CreateLogger<SerialSession>());
        }
        catch (OperationCanceledException exception)
        {
            _ = DisposeLateConnectionAsync(opening);
            throw new ProtocolException(cancellationToken.IsCancellationRequested
                    ? "Opening the serial port was cancelled." : "Opening the serial port timed out.",
                cancellationToken.IsCancellationRequested ? ProtocolFailureKind.Cancelled : ProtocolFailureKind.Timeout, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or TimeoutException)
        {
            throw MapError(exception, options.PortName);
        }
    }

    internal static ProtocolException MapError(Exception exception, string port) => new(
        $"Serial port {port}: {exception.Message}", exception switch
        {
            ArgumentException or NotSupportedException => ProtocolFailureKind.Configuration,
            UnauthorizedAccessException or InvalidOperationException => ProtocolFailureKind.ServerRejected,
            TimeoutException => ProtocolFailureKind.Timeout,
            _ => ProtocolFailureKind.Network,
        }, exception);

    private static async Task DisposeLateConnectionAsync(Task<ISerialConnection> opening)
    {
        try { (await opening.ConfigureAwait(false)).Dispose(); }
        catch { /* Observe a late open failure; no session was published. */ }
    }

    private static IReadOnlyList<string> DiscoverPorts()
    {
        try { return SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        { return []; }
    }
}
