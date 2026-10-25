namespace SurgeLink.Terminal;

/// <summary>
/// Bounded ring buffer holding the lines that scrolled off the top of the
/// primary screen. When the limit is reached the oldest line is recycled.
/// </summary>
internal sealed class ScrollbackBuffer
{
    private int _limit;
    private CellLine?[] _items;
    private int _head;
    private int _count;

    public ScrollbackBuffer(int limit)
    {
        _limit = limit;
        _items = new CellLine?[Math.Max(1, Math.Min(limit, 64))];
    }

    /// <summary>Number of stored lines (older lines first).</summary>
    public int Count => _count;

    /// <summary>Configured cap.</summary>
    public int Limit => _limit;

    public void SetLimit(int limit)
    {
        var keep = Math.Min(_count, limit);
        var items = new CellLine?[Math.Max(1, Math.Max(keep, Math.Min(limit, 64)))];
        for (var index = 0; index < keep; index++) items[index] = this[_count - keep + index];
        _items = items;
        _head = 0;
        _count = keep;
        _limit = limit;
    }

    /// <summary>Reads a stored line by index; index 0 is the oldest line.</summary>
    public CellLine this[int index] => _items[(_head + index) % _items.Length]!;

    /// <summary>
    /// Stores a line. Returns a line the caller may recycle, or <c>null</c>
    /// when the line was stored and nothing was evicted.
    /// </summary>
    public CellLine? Add(CellLine line)
    {
        if (_limit == 0)
        {
            return line;
        }

        if (_count == _limit)
        {
            CellLine evicted = _items[_head]!;
            _items[_head] = line;
            _head = (_head + 1) % _items.Length;
            return evicted;
        }

        if (_count == _items.Length)
        {
            Grow();
        }

        _items[(_head + _count) % _items.Length] = line;
        _count++;
        return null;
    }

    /// <summary>Drops every stored line.</summary>
    public void Clear()
    {
        Array.Clear(_items);
        _head = 0;
        _count = 0;
    }

    private void Grow()
    {
        int capacity = Math.Min(_limit, Math.Max(2, _items.Length * 2));
        var grown = new CellLine?[capacity];
        for (int i = 0; i < _count; i++)
        {
            grown[i] = _items[(_head + i) % _items.Length];
        }

        _items = grown;
        _head = 0;
    }
}
