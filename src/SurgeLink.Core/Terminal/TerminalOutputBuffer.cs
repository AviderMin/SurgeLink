using System.Text;

namespace SurgeLink.Core.Terminal;

/// <summary>Bounded incremental UTF-8 decoding independent of screen repaint and scrollback.</summary>
public sealed class TerminalOutputBuffer(int capacity = 131072)
{
    private readonly object _gate = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] _text = new char[capacity >= 2 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
    private int _head;
    private int _length;
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
                // Copy at most two contiguous spans; retained output never moves.
                var retained = Math.Min(written, _capacity);
                var evicted = Math.Max(0, _length + retained - _capacity);
                _head = (_head + evicted) % _capacity;
                _length -= evicted;
                var tail = (_head + _length) % _capacity;
                var source = chars.Slice(written - retained, retained);
                var first = Math.Min(retained, _capacity - tail);
                source[..first].CopyTo(_text.AsSpan(tail));
                source[first..].CopyTo(_text);
                _length += retained;
                _end += written;
                bytes = bytes[count..];
                if (_length > 0 && char.IsLowSurrogate(_text[_head]))
                {
                    _head = (_head + 1) % _capacity;
                    _length--;
                }
            }
        }
    }

    public TerminalOutputPage Read(long? cursor, int maximumCharacters = 16384)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 2);
        lock (_gate)
        {
            var oldest = _end - _length;
            var requested = cursor ?? oldest;
            if (requested < 0 || requested > _end) throw new ArgumentOutOfRangeException(nameof(cursor));
            var start = Math.Max(requested, oldest);
            var index = (int)(start - oldest);
            var gap = requested < oldest;
            if (index < _length && char.IsLowSurrogate(_text[(_head + index) % _capacity])) { index++; start++; gap = true; }
            var count = Math.Min(maximumCharacters, _length - index);
            if (count > 0 && char.IsHighSurrogate(_text[(_head + index + count - 1) % _capacity])) count--;
            var next = start + count;
            var text = string.Create(count, (Buffer: _text, Offset: (_head + index) % _capacity), static (destination, state) =>
            {
                var first = Math.Min(destination.Length, state.Buffer.Length - state.Offset);
                state.Buffer.AsSpan(state.Offset, first).CopyTo(destination);
                state.Buffer.AsSpan(0, destination.Length - first).CopyTo(destination[first..]);
            });
            return new(text, next, oldest, _end, gap, next < _end);
        }
    }
}
