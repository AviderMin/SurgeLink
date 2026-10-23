using System.Text;

namespace SurgeLink.Protocols.Telnet;

/// <summary>
/// Incremental RFC 854 parser. Negotiation and application data share one stream;
/// parser state survives arbitrary TCP packet boundaries. Call under the terminal's I/O lock.
/// </summary>
internal sealed class TelnetCodec
{
    private const byte Iac = 255, Will = 251, Wont = 252, Do = 253, Dont = 254, Sb = 250, Se = 240;
    private const byte Binary = 0, Echo = 1, SuppressGoAhead = 3, TerminalType = 24, WindowSize = 31;
    private readonly OptionState[] _local = new OptionState[256];
    private readonly OptionState[] _remote = new OptionState[256];
    private readonly List<byte> _subnegotiation = new();
    private readonly string _terminalType;
    private ParserState _state;
    private byte _command;
    private bool _subnegotiationOverflow;
    private bool _afterCarriageReturn;

    public TelnetCodec(int columns, int rows, string terminalType)
    {
        Columns = Math.Clamp(columns, 1, ushort.MaxValue);
        Rows = Math.Clamp(rows, 1, ushort.MaxValue);
        _terminalType = string.IsNullOrWhiteSpace(terminalType) ? "xterm-256color" : terminalType;
        if (_terminalType.Length > 128 || _terminalType.Any(c => c is < ' ' or > '~'))
            throw new ArgumentException("Terminal type must contain at most 128 printable ASCII characters.", nameof(terminalType));
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }

    public byte[] BeginNegotiation()
    {
        var reply = new List<byte>();
        foreach (byte option in new[] { Binary, SuppressGoAhead, TerminalType, WindowSize })
        {
            _local[option] = OptionState.WantYes;
            reply.AddRange([Iac, Will, option]);
        }
        foreach (byte option in new[] { Binary, Echo, SuppressGoAhead })
        {
            _remote[option] = OptionState.WantYes;
            reply.AddRange([Iac, Do, option]);
        }
        return reply.ToArray();
    }

    public (byte[] Data, byte[] Reply) Receive(ReadOnlySpan<byte> bytes)
    {
        var data = new List<byte>(bytes.Length);
        var reply = new List<byte>();
        foreach (byte value in bytes)
        {
            switch (_state)
            {
                case ParserState.Data:
                    if (value == Iac) _state = ParserState.Command;
                    else AddData(value, data);
                    break;
                case ParserState.Command:
                    if (value == Iac) { AddData(value, data); _state = ParserState.Data; }
                    else if (value is Will or Wont or Do or Dont) { _command = value; _state = ParserState.Option; }
                    else if (value == Sb)
                    {
                        _subnegotiation.Clear();
                        _subnegotiationOverflow = false;
                        _state = ParserState.Subnegotiation;
                    }
                    else _state = ParserState.Data; // NOP, GA and other standalone commands.
                    break;
                case ParserState.Option:
                    Negotiate(_command, value, reply);
                    _state = ParserState.Data;
                    break;
                case ParserState.Subnegotiation:
                    if (value == Iac) _state = ParserState.SubnegotiationCommand;
                    else AddSubnegotiation(value);
                    break;
                case ParserState.SubnegotiationCommand:
                    if (value == Se)
                    {
                        if (!_subnegotiationOverflow && _subnegotiation.Count == 2 &&
                            _subnegotiation[0] == TerminalType && _subnegotiation[1] == 1 &&
                            _local[TerminalType] == OptionState.Yes)
                        {
                            SendSubnegotiation(TerminalType, [0, .. Encoding.ASCII.GetBytes(_terminalType)], reply);
                        }
                        _state = ParserState.Data;
                    }
                    else
                    {
                        if (value == Iac) AddSubnegotiation(Iac);
                        else _subnegotiationOverflow = true;
                        _state = ParserState.Subnegotiation;
                    }
                    break;
            }
        }
        return (data.ToArray(), reply.ToArray());
    }

    public byte[] Encode(ReadOnlySpan<byte> bytes)
    {
        var result = new List<byte>(bytes.Length);
        bool binary = _local[Binary] == OptionState.Yes;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte value = bytes[i];
            if (!binary && value == '\r')
            {
                result.Add((byte)'\r');
                if (i + 1 < bytes.Length && bytes[i + 1] is (byte)'\n' or 0) result.Add(bytes[++i]);
                else result.Add(0);
            }
            else if (!binary && value == '\n') result.AddRange([(byte)'\r', (byte)'\n']);
            else AddEscaped(value, result);
        }
        return result.ToArray();
    }

    public byte[] Resize(int columns, int rows)
    {
        Columns = Math.Clamp(columns, 1, ushort.MaxValue);
        Rows = Math.Clamp(rows, 1, ushort.MaxValue);
        var reply = new List<byte>();
        if (_local[WindowSize] == OptionState.Yes) SendWindowSize(reply);
        return reply.ToArray();
    }

    private void Negotiate(byte command, byte option, List<byte> reply)
    {
        bool local = command is Do or Dont;
        var states = local ? _local : _remote;
        byte positive = local ? Will : Do;
        byte negative = local ? Wont : Dont;
        bool enable = command is Do or Will;
        if (enable)
        {
            if (states[option] == OptionState.Yes) return; // Acknowledge changes only, never loop.
            bool supported = local
                ? option is Binary or SuppressGoAhead or TerminalType or WindowSize
                : option is Binary or Echo or SuppressGoAhead;
            if (!supported) { reply.AddRange([Iac, negative, option]); return; }
            if (states[option] == OptionState.No) reply.AddRange([Iac, positive, option]);
            states[option] = OptionState.Yes;
            if (local && option == WindowSize) SendWindowSize(reply);
        }
        else
        {
            if (states[option] == OptionState.Yes) reply.AddRange([Iac, negative, option]);
            states[option] = OptionState.No;
        }
    }

    private void AddData(byte value, List<byte> data)
    {
        bool skip = _remote[Binary] != OptionState.Yes && _afterCarriageReturn && value == 0;
        _afterCarriageReturn = value == '\r';
        if (!skip) data.Add(value);
    }

    private void AddSubnegotiation(byte value)
    {
        // A malicious or broken peer must not grow an unterminated SB indefinitely.
        if (_subnegotiation.Count < 1024) _subnegotiation.Add(value);
        else _subnegotiationOverflow = true;
    }

    private void SendWindowSize(List<byte> reply) => SendSubnegotiation(WindowSize,
        [(byte)(Columns >> 8), (byte)Columns, (byte)(Rows >> 8), (byte)Rows], reply);

    private static void SendSubnegotiation(byte option, ReadOnlySpan<byte> payload, List<byte> reply)
    {
        reply.AddRange([Iac, Sb, option]);
        foreach (byte value in payload) AddEscaped(value, reply);
        reply.AddRange([Iac, Se]);
    }

    private static void AddEscaped(byte value, List<byte> result)
    {
        result.Add(value);
        if (value == Iac) result.Add(Iac);
    }

    // No user-driven option toggling: only initial requests and peer-driven changes.
    // WantYes distinguishes acknowledgements from unsolicited positive requests.
    private enum OptionState { No, WantYes, Yes }
    private enum ParserState { Data, Command, Option, Subnegotiation, SubnegotiationCommand }
}
