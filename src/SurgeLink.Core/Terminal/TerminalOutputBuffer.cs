using System.Text;

namespace SurgeLink.Core.Terminal;

/// <summary>Bounded incremental UTF-8 decoding independent of screen repaint and scrollback.</summary>
public sealed class TerminalOutputBuffer(int capacity = 131072)
{
    private readonly object _gate = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _text = new();
    private readonly int _capacity = capacity >= 2 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private long _end;

    public long Cursor { get { lock (_gate) return _end; } }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            Span<char> chars = stackalloc char[4098];
            while (!bytes.IsEmpty)
            {
                var count = Math.Min(bytes.Length, 4096);
                var written = _decoder.GetChars(bytes[..count], chars, flush: false);
                _text.Append(chars[..written]);
                _end += written;
                bytes = bytes[count..];
                if (_text.Length > _capacity)
                {
                    _text.Remove(0, _text.Length - _capacity);
                    if (_text.Length > 0 && char.IsLowSurrogate(_text[0])) _text.Remove(0, 1);
                }
            }
        }
    }

    public TerminalOutputPage Read(long? cursor, int maximumCharacters = 16384)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 2);
        lock (_gate)
        {
            var oldest = _end - _text.Length;
            var requested = cursor ?? oldest;
            if (requested < 0 || requested > _end) throw new ArgumentOutOfRangeException(nameof(cursor));
            var start = Math.Max(requested, oldest);
            var index = (int)(start - oldest);
            var gap = requested < oldest;
            if (index < _text.Length && char.IsLowSurrogate(_text[index])) { index++; start++; gap = true; }
            var count = Math.Min(maximumCharacters, _text.Length - index);
            if (count > 0 && char.IsHighSurrogate(_text[index + count - 1])) count--;
            var next = start + count;
            return new(_text.ToString(index, count), next, oldest, _end, gap, next < _end);
        }
    }
}
