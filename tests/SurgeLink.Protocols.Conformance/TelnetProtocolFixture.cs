using System.Net;
using System.Net.Sockets;
using System.Text;
using SurgeLink.Core.Protocols;
using SurgeLink.Protocols.Telnet;

namespace SurgeLink.Protocols.Conformance;

/// <summary>Runs the actual Telnet transport against a process-local terminal server.</summary>
public sealed class TelnetProtocolFixture : ProtocolFixture
{
    // The registry retains fixtures for the test process. No external endpoint is needed.
    private static readonly TcpListener Listener = StartServer();

    public override string Name => "telnet";
    public override IProtocolPlugin Plugin { get; } = new TelnetProtocolPlugin();

    public override ConnectionRequest CreateConnectionRequest() => new()
    {
        Host = "127.0.0.1",
        Port = ((IPEndPoint)Listener.LocalEndpoint).Port,
        Username = "conformance",
        KeepAliveInterval = TimeSpan.Zero,
    };

    private static TcpListener StartServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = ServeAsync(client);
            }
        });
        return listener;
    }

    private static async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("Telnet conformance server\r\n"));
                var buffer = new byte[4096];
                int state = 0;
                while (true)
                {
                    int count = await stream.ReadAsync(buffer);
                    if (count == 0) return;
                    var echo = new List<byte>();
                    // Ignore options (this server keeps NVT defaults) and echo application data.
                    for (int i = 0; i < count; i++)
                    {
                        byte value = buffer[i];
                        switch (state)
                        {
                            case 0:
                                if (value == 255) state = 1;
                                else echo.Add(value);
                                break;
                            case 1:
                                if (value is >= 251 and <= 254) state = 2;
                                else if (value == 250) state = 3;
                                else { if (value == 255) echo.AddRange([255, 255]); state = 0; }
                                break;
                            case 2: state = 0; break;
                            case 3: if (value == 255) state = 4; break;
                            case 4: state = value == 240 ? 0 : 3; break;
                        }
                    }
                    if (echo.Count > 0) await stream.WriteAsync(echo.ToArray());
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
                // Client teardown is expected during conformance lifecycle checks.
            }
        }
    }
}
