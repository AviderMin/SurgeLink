namespace SurgeLink.Core.Files;

/// <summary>Logical line starts in the native editor buffer, built only when text changes.</summary>
public sealed class EditorLineIndex
{
    private readonly List<int> _starts = [0];
    private string _text = string.Empty;

    public EditorLineIndex(string text) => Update(text);

    /// <summary>Reuses the unchanged line starts before the first edit.</summary>
    public void Update(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int prefix = _text.AsSpan().CommonPrefixLength(text.AsSpan());
        if (prefix == _text.Length && prefix == text.Length) { return; }
        int line = _starts.BinarySearch(Math.Max(0, prefix - 1));
        if (line < 0) { line = ~line - 1; }
        _starts.RemoveRange(line + 1, _starts.Count - line - 1);
        for (var offset = _starts[line]; offset < text.Length; offset++)
        {
            if (text[offset] == '\r')
            {
                if (offset + 1 < text.Length && text[offset + 1] == '\n') { offset++; }
                _starts.Add(offset + 1);
            }
            else if (text[offset] == '\n') { _starts.Add(offset + 1); }
        }
        _text = text;
    }

    public int Count => _starts.Count;
    public int this[int line] => _starts[line];

    /// <summary>
    /// Finds the first line whose first visual row ends below the viewport top.
    /// Native geometry accounts for wrapping; only logarithmically many rows are queried.
    /// Count means all line labels have scrolled above the viewport.
    /// </summary>
    public int FirstVisibleLine(Func<int, double> getLineBottom, double viewportTop = 0)
    {
        ArgumentNullException.ThrowIfNull(getLineBottom);
        var low = 0;
        var high = Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (getLineBottom(_starts[middle]) <= viewportTop) { low = middle + 1; }
            else { high = middle; }
        }
        return low;
    }
}
