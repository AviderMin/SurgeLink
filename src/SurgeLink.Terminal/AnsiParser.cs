using System.Text;

namespace SurgeLink.Terminal;

/// <summary>
/// Byte level VT/xterm parser. All state survives between <see cref="Feed"/>
/// calls, so control sequences split across transport chunks parse correctly.
/// The parser only recognises structure; semantics live in
/// <see cref="TerminalEmulator"/>.
/// </summary>
internal sealed class AnsiParser
{
    private const int MaxParameters = 32;
    private const int MaxIntermediates = 2;
    private const int MaxOscBytes = 4096;
    private const byte EscapeByte = 0x1B;

    private enum ParserState
    {
        Ground,
        Escape,
        Csi,
        Osc,
        ConsumeString,
    }

    private readonly TerminalEmulator _terminal;
    private readonly Utf8Decoder _utf8 = new();
    private readonly int[] _parameters = new int[MaxParameters];
    private readonly bool[] _parameterIsSub = new bool[MaxParameters];
    private readonly char[] _intermediates = new char[MaxIntermediates];
    private readonly byte[] _oscBytes = new byte[MaxOscBytes];

    private ParserState _state;
    private int _parameterCount;
    private int _currentParameter;
    private bool _hasCurrentParameter;
    private bool _currentIsSubParameter;
    private bool _ignoreSequence;
    private char _privateMarker;
    private int _intermediateCount;
    private int _oscLength;
    private bool _oscEscape;
    private bool _stringEscape;

    public AnsiParser(TerminalEmulator terminal) => _terminal = terminal;

    /// <summary>Drops any partial sequence and returns to the ground state.</summary>
    public void Reset()
    {
        _state = ParserState.Ground;
        _utf8.Reset();
        ResetSequence();
        _oscLength = 0;
        _oscEscape = false;
        _stringEscape = false;
    }

    /// <summary>Consumes a chunk of bytes, running sequences as they complete.</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            switch (_state)
            {
                case ParserState.Ground:
                    FeedGround(value);
                    break;
                case ParserState.Escape:
                    FeedEscape(value);
                    break;
                case ParserState.Csi:
                    FeedCsi(value);
                    break;
                case ParserState.Osc:
                    FeedOsc(value);
                    break;
                default:
                    FeedConsumeString(value);
                    break;
            }
        }
    }

    private void FeedGround(byte value)
    {
        if (value >= 0x80)
        {
            _utf8.Feed(value);
            DrainDecodedRunes();
            return;
        }

        FlushPendingDecode();
        if (value == EscapeByte)
        {
            ResetSequence();
            _state = ParserState.Escape;
            return;
        }

        if (value < 0x20)
        {
            _terminal.ExecuteControl(value);
            return;
        }

        if (value == 0x7F)
        {
            return;
        }

        _terminal.PrintAscii(value);
    }

    private void FeedEscape(byte value)
    {
        if (value == EscapeByte)
        {
            ResetSequence();
            return;
        }

        if (value < 0x20)
        {
            if (value is 0x18 or 0x1A)
            {
                _state = ParserState.Ground;
                ResetSequence();
            }

            return;
        }

        if (value is >= 0x20 and <= 0x2F)
        {
            if (_intermediateCount < MaxIntermediates)
            {
                _intermediates[_intermediateCount++] = (char)value;
            }
            else
            {
                _ignoreSequence = true;
            }

            return;
        }

        if (value is >= 0x30 and <= 0x7E)
        {
            _state = ParserState.Ground;
            if (_ignoreSequence)
            {
                ResetSequence();
                return;
            }

            switch (value)
            {
                case (byte)'[':
                    ResetSequence();
                    _state = ParserState.Csi;
                    return;
                case (byte)']':
                    ResetSequence();
                    _oscLength = 0;
                    _oscEscape = false;
                    _state = ParserState.Osc;
                    return;
                case (byte)'P':
                case (byte)'X':
                case (byte)'^':
                case (byte)'_':
                    ResetSequence();
                    _stringEscape = false;
                    _state = ParserState.ConsumeString;
                    return;
                default:
                    _terminal.HandleEsc((char)value, _intermediates.AsSpan(0, _intermediateCount));
                    ResetSequence();
                    return;
            }
        }

        _state = ParserState.Ground;
        ResetSequence();
    }

    private void FeedCsi(byte value)
    {
        if (value == EscapeByte)
        {
            ResetSequence();
            _state = ParserState.Escape;
            return;
        }

        if (value < 0x20)
        {
            if (value is 0x18 or 0x1A)
            {
                _state = ParserState.Ground;
                ResetSequence();
            }

            return;
        }

        if (value is >= 0x40 and <= 0x7E)
        {
            _state = ParserState.Ground;
            DispatchCsi((char)value);
            return;
        }

        if (value is >= 0x20 and <= 0x2F)
        {
            // Intermediates may follow parameters (for example DECSCUSR is
            // CSI Ps SP q); any parameter after an intermediate is invalid and
            // is handled below.
            if (_intermediateCount >= MaxIntermediates)
            {
                _ignoreSequence = true;
                return;
            }

            _intermediates[_intermediateCount++] = (char)value;
            return;
        }

        if (value is >= 0x30 and <= 0x3F)
        {
            if (_intermediateCount > 0)
            {
                _ignoreSequence = true;
                return;
            }

            switch (value)
            {
                case (byte)';':
                    PushParameter();
                    return;
                case (byte)':':
                    PushParameter();
                    _currentIsSubParameter = true;
                    return;
                case >= 0x30 and <= 0x39:
                    if (!_ignoreSequence)
                    {
                        _currentParameter = Math.Min(9999, (_currentParameter * 10) + (value - 0x30));
                        _hasCurrentParameter = true;
                    }

                    return;
                default:
                    if (!_ignoreSequence && _parameterCount == 0 && !_hasCurrentParameter && _privateMarker == '\0')
                    {
                        _privateMarker = (char)value;
                    }
                    else
                    {
                        _ignoreSequence = true;
                    }

                    return;
            }
        }
    }

    private void FeedOsc(byte value)
    {
        if (_oscEscape)
        {
            _oscEscape = false;
            if (value == (byte)'\\')
            {
                // ESC \ is the string terminator: dispatch the payload.
                FinishOsc();
                return;
            }

            // A non-ST escape aborts the string. Dispatch what was collected so far
            // and hand the byte to the normal escape handling: an OSC followed by
            // ESC [ is a new CSI, not more title text. Otherwise the following
            // control sequence and plain text would be swallowed until a BEL/ST.
            FinishOsc();
            FeedEscape(value);
            return;
        }

        if (value == EscapeByte)
        {
            _oscEscape = true;
            return;
        }

        if (value == 0x07)
        {
            FinishOsc();
            return;
        }

        if (value is 0x18 or 0x1A)
        {
            _state = ParserState.Ground;
            _oscLength = 0;
            _oscEscape = false;
            return;
        }

        if (_oscLength < MaxOscBytes)
        {
            _oscBytes[_oscLength++] = value;
        }
    }

    private void FinishOsc()
    {
        int length = _oscLength;
        _state = ParserState.Ground;
        _oscLength = 0;
        _oscEscape = false;
        if (length > 0)
        {
            _terminal.HandleOsc(_oscBytes.AsSpan(0, length));
        }
    }

    private void FeedConsumeString(byte value)
    {
        if (_stringEscape)
        {
            _stringEscape = false;
            if (value == (byte)'\\')
            {
                // ESC \ is ST: the control string is complete.
                _state = ParserState.Ground;
                return;
            }

            // A non-ST escape aborts the unsupported string instead of treating
            // the rest of the stream as payload. FeedEscape then decides whether
            // the byte introduces a new CSI/OSC/string or is a plain final.
            _state = ParserState.Ground;
            FeedEscape(value);
            return;
        }

        if (value == EscapeByte)
        {
            _stringEscape = true;
            return;
        }

        if (value is 0x18 or 0x1A)
        {
            _stringEscape = false;
            _state = ParserState.Ground;
        }
    }

    private void DrainDecodedRunes()
    {
        while (_utf8.TryDequeue(out Rune rune))
        {
            _terminal.PrintRune(rune);
        }
    }

    private void FlushPendingDecode()
    {
        if (!_utf8.HasPending)
        {
            return;
        }

        _utf8.FlushPending();
        DrainDecodedRunes();
    }

    private void PushParameter()
    {
        if (_parameterCount >= MaxParameters)
        {
            _ignoreSequence = true;
        }
        else
        {
            _parameters[_parameterCount] = _hasCurrentParameter ? _currentParameter : 0;
            _parameterIsSub[_parameterCount] = _currentIsSubParameter;
            _parameterCount++;
        }

        _currentParameter = 0;
        _hasCurrentParameter = false;
        _currentIsSubParameter = false;
    }

    private void DispatchCsi(char final)
    {
        if (_hasCurrentParameter || _parameterCount > 0)
        {
            PushParameter();
        }

        if (!_ignoreSequence)
        {
            _terminal.HandleCsi(
                final,
                _privateMarker,
                _intermediates.AsSpan(0, _intermediateCount),
                _parameters.AsSpan(0, _parameterCount),
                _parameterIsSub.AsSpan(0, _parameterCount));
        }

        ResetSequence();
    }

    private void ResetSequence()
    {
        _parameterCount = 0;
        _currentParameter = 0;
        _hasCurrentParameter = false;
        _currentIsSubParameter = false;
        _privateMarker = '\0';
        _intermediateCount = 0;
        _ignoreSequence = false;
    }
}
