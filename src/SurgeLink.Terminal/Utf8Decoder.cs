using System.Buffers;
using System.Text;

namespace SurgeLink.Terminal;

/// <summary>
/// Incremental UTF-8 decoder. Bytes may arrive in arbitrary chunks: incomplete
/// sequences are buffered until the next feed, invalid input produces U+FFFD
/// and never throws.
/// </summary>
internal sealed class Utf8Decoder
{
    private const char ReplacementCharacter = '\uFFFD';

    private readonly byte[] _sequence = new byte[4];
    private readonly Rune[] _ready = new Rune[4];
    private int _sequenceLength;
    private int _readyCount;
    private int _readyIndex;

    /// <summary>True when a partial sequence is buffered.</summary>
    public bool HasPending => _sequenceLength > 0;

    /// <summary>Feeds one byte; zero, one or two runes become available.</summary>
    public void Feed(byte value)
    {
        if (_sequenceLength == 0)
        {
            if (value < 0x80)
            {
                Emit(new Rune(value));
                return;
            }

            if (ExpectedLength(value) == 0)
            {
                Emit(new Rune(ReplacementCharacter));
                return;
            }

            _sequence[0] = value;
            _sequenceLength = 1;
            return;
        }

        if ((value & 0xC0) == 0x80)
        {
            _sequence[_sequenceLength++] = value;
            if (_sequenceLength == ExpectedLength(_sequence[0]))
            {
                CompleteSequence();
            }

            return;
        }

        // A non continuation byte aborts the pending sequence; the new byte is
        // then decoded as if it started a fresh sequence.
        _sequenceLength = 0;
        Emit(new Rune(ReplacementCharacter));
        Feed(value);
    }

    /// <summary>Emits U+FFFD for an incomplete sequence that is being abandoned.</summary>
    public void FlushPending()
    {
        if (_sequenceLength == 0)
        {
            return;
        }

        _sequenceLength = 0;
        Emit(new Rune(ReplacementCharacter));
    }

    /// <summary>Drops buffered bytes and pending runes.</summary>
    public void Reset()
    {
        _sequenceLength = 0;
        _readyCount = 0;
        _readyIndex = 0;
    }

    /// <summary>Takes the next decoded rune, if any.</summary>
    public bool TryDequeue(out Rune rune)
    {
        if (_readyIndex >= _readyCount)
        {
            _readyCount = 0;
            _readyIndex = 0;
            rune = default;
            return false;
        }

        rune = _ready[_readyIndex++];
        if (_readyIndex == _readyCount)
        {
            _readyCount = 0;
            _readyIndex = 0;
        }

        return true;
    }

    private void CompleteSequence()
    {
        ReadOnlySpan<byte> span = _sequence.AsSpan(0, _sequenceLength);
        OperationStatus status = Rune.DecodeFromUtf8(span, out Rune rune, out int consumed);
        _sequenceLength = 0;
        Emit(status == OperationStatus.Done && consumed == span.Length ? rune : new Rune(ReplacementCharacter));
    }

    private static int ExpectedLength(byte first) => first switch
    {
        >= 0xC2 and <= 0xDF => 2,
        >= 0xE0 and <= 0xEF => 3,
        >= 0xF0 and <= 0xF4 => 4,
        _ => 0,
    };

    private void Emit(Rune rune)
    {
        if (_readyCount < _ready.Length)
        {
            _ready[_readyCount++] = rune;
        }
    }
}
