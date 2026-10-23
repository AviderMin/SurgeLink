using System.IO.Ports;

namespace SurgeLink.Protocols.Serial;

/// <summary>Bounded synchronous I/O keeps cancellation independent of driver async support.</summary>
internal interface ISerialConnection : IDisposable
{
    void Open();
    int Read(byte[] buffer);
    void Write(byte[] data);
}

internal sealed class SerialPortConnection : ISerialConnection
{
    private readonly SerialPort _port;

    public SerialPortConnection(SerialOptions options)
    {
        _port = new SerialPort(options.PortName, options.BaudRate, options.Parity, options.DataBits, options.StopBits)
        {
            Handshake = options.Handshake,
            DtrEnable = options.DtrEnable,
            ReadTimeout = 200,
            WriteTimeout = 2000,
        };
        if (options.Handshake is not (Handshake.RequestToSend or Handshake.RequestToSendXOnXOff))
            _port.RtsEnable = options.RtsEnable;
    }

    public void Open() => _port.Open();
    public int Read(byte[] buffer) => _port.Read(buffer, 0, buffer.Length);
    public void Write(byte[] data) => _port.Write(data, 0, data.Length);
    public void Dispose() => _port.Dispose();
}
