namespace SurgeLink.Terminal;

/// <summary>
/// One text match found by <see cref="ITerminalEmulator.FindText"/>. Lines use an
/// absolute coordinate: 0 is the oldest retained scrollback line and viewport rows
/// follow after <see cref="ITerminalEmulator.ScrollbackLength"/>. A match may live
/// outside the current viewport; callers scroll it into view themselves.
/// </summary>
/// <param name="Line">Zero-based absolute line index.</param>
/// <param name="StartColumn">First viewport column covered by the match.</param>
/// <param name="Length">Number of columns covered; a wide glyph counts as two.</param>
public readonly record struct TerminalSearchMatch(int Line, int StartColumn, int Length)
{
    /// <summary>Last column covered by the match, inclusive.</summary>
    public int EndColumn => StartColumn + Length - 1;
}
