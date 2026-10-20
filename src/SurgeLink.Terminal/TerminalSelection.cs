namespace SurgeLink.Terminal;

/// <summary>A cell position inside the viewport.</summary>
public readonly record struct TerminalPosition(int Row, int Column);

/// <summary>A rectangular/linear text selection in viewport coordinates.</summary>
public readonly record struct TerminalSelection(TerminalPosition Start, TerminalPosition End)
{
    /// <summary>True when start and end describe the same cell.</summary>
    public bool IsEmpty => Start == End;

    /// <summary>Normalised pair with start before end.</summary>
    public (TerminalPosition First, TerminalPosition Last) Normalized() =>
        Start.Row < End.Row || (Start.Row == End.Row && Start.Column <= End.Column) ? (Start, End) : (End, Start);

    /// <summary>Creates a selection from drag anchors.</summary>
    public static TerminalSelection FromAnchors(TerminalPosition anchor, TerminalPosition caret) => new(anchor, caret);
}
