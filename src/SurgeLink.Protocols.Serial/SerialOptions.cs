using System.Globalization;
using System.IO.Ports;
using SurgeLink.Core.Protocols;

namespace SurgeLink.Protocols.Serial;

internal sealed record SerialOptions(
    string PortName, int BaudRate, int DataBits, Parity Parity, StopBits StopBits,
    Handshake Handshake, bool DtrEnable, bool RtsEnable)
{
    public static IReadOnlyList<ProtocolOptionDescriptor> Fields { get; } =
    [
        new("serial.baudRate", "Baud rate", "115200") { Minimum = 1, Maximum = int.MaxValue },
        new("serial.dataBits", "Data bits", "8") { Choices = ["5", "6", "7", "8"] },
        new("serial.parity", "Parity", "None") { Choices = ["None", "Odd", "Even", "Mark", "Space"] },
        new("serial.stopBits", "Stop bits", "One")
        {
            Choices = ["One", "OnePointFive", "Two"],
            ChoiceLabels = new Dictionary<string, string> { ["One"] = "1", ["OnePointFive"] = "1.5", ["Two"] = "2" },
        },
        new("serial.handshake", "Flow control", "None")
        {
            Choices = ["None", "XOnXOff", "RequestToSend", "RequestToSendXOnXOff"],
            ChoiceLabels = new Dictionary<string, string>
            {
                ["XOnXOff"] = "XON/XOFF", ["RequestToSend"] = "RTS/CTS", ["RequestToSendXOnXOff"] = "RTS/CTS + XON/XOFF",
            },
        },
        new("serial.dtrEnable", "Enable DTR", "false") { IsBoolean = true },
        new("serial.rtsEnable", "Enable RTS (without hardware flow control)", "false") { IsBoolean = true },
    ];

    public static SerialOptions Parse(ConnectionRequest request)
    {
        string port = request.Host?.Trim() ?? string.Empty;
        // This Windows app accepts COM device names, never arbitrary device/file paths.
        if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
            port.Length <= 3 || port.AsSpan(3).ContainsAnyExceptInRange('0', '9') ||
            !int.TryParse(port.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1)
            throw Configuration("Serial port must be a COM device name such as COM3.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            string value = request.Options.TryGetValue(field.Key, out string? configured) ? configured?.Trim() ?? string.Empty : field.DefaultValue;
            if (!field.IsValid(value)) throw Configuration($"Invalid serial setting: {field.Label}.");
            values[field.Key] = value;
        }
        var options = new SerialOptions("COM" + number.ToString(CultureInfo.InvariantCulture),
            int.Parse(values["serial.baudRate"], CultureInfo.InvariantCulture),
            int.Parse(values["serial.dataBits"], CultureInfo.InvariantCulture),
            Enum.Parse<Parity>(values["serial.parity"], true),
            Enum.Parse<StopBits>(values["serial.stopBits"], true),
            Enum.Parse<Handshake>(values["serial.handshake"], true),
            bool.Parse(values["serial.dtrEnable"]), bool.Parse(values["serial.rtsEnable"]));
        if ((options.StopBits == StopBits.OnePointFive && options.DataBits != 5) ||
            (options.StopBits == StopBits.Two && options.DataBits == 5))
            throw Configuration("1.5 stop bits requires 5 data bits; 2 stop bits requires 6, 7 or 8 data bits.");
        if (options.RtsEnable && options.Handshake is Handshake.RequestToSend or Handshake.RequestToSendXOnXOff)
            throw Configuration("RTS is controlled by hardware flow control; turn off manual RTS.");
        return options;
    }

    private static ProtocolException Configuration(string message) => new(message, ProtocolFailureKind.Configuration);
}
