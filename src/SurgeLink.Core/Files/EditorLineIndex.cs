namespace SurgeLink.Core.Files;

/// <summary>Logical line starts in the native editor buffer, built only when text changes.</summary>
public sealed class EditorLineIndex
{
    private readonly int[] _starts;

    public EditorLineIndex(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var starts = new List<int> { 0 };
        for (var offset = 0; offset < text.Length; offset++)
        {
            if (text[offset] == '\r')
            {
                if (offset + 1 < text.Length && text[offset + 1] == '\n') { offset++; }
                starts.Add(offset + 1);
            }
            else if (text[offset] == '\n') { starts.Add(offset + 1); }
        }
        _starts = starts.ToArray();
    }

    public int Count => _starts.Length;
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
