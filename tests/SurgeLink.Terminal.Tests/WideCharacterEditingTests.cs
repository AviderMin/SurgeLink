namespace SurgeLink.Terminal.Tests;

/// <summary>
/// Editing and resize operations must keep the wide-glyph invariant: every
/// continuation cell has a wide cell immediately to its left, and every wide
/// cell has a continuation immediately to its right.
/// </summary>
public sealed class WideCharacterEditingTests
{
    [Fact]
    public void DeletingAcrossAWideGlyphBoundaryLeavesNoOrphanContinuation()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("A中B");
        terminal.Feed("\u001b[1;1H\u001b[2P");

        AssertValidWidePairs(terminal);
        // DCH 2 removes two cell positions; the truncated wide glyph leaves the
        // first column blank and the following B must land one column right.
        Assert.Equal(" B", terminal.Line(0));
        Assert.Equal("B", terminal.CellText(0, 1));
    }

    [Fact]
    public void DeletingFromTheContinuationHalfClearsTheWholeGlyph()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("A中B");
        terminal.Feed("\u001b[1;3H\u001b[1P");

        AssertValidWidePairs(terminal);
        Assert.NotEqual(CellWidth.Continuation, terminal.Cell(0, 1).Width);
    }

    [Fact]
    public void NarrowingTheWindowDoesNotDiscardVisibleContentWhenItWidensBack()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("abcdefghij");

        terminal.Emulator.Resize(5, 3);
        Assert.Equal("abcde", terminal.Line(0));

        terminal.Emulator.Resize(10, 3);
        Assert.Equal("abcdefghij", terminal.Line(0));
    }

    [Fact]
    public void WideningBeyondTheOriginalWidthRestoresTheHiddenPrefix()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("abcdefghij");

        terminal.Emulator.Resize(5, 3);
        terminal.Emulator.Resize(12, 3);

        Assert.Equal(12, terminal.Emulator.Columns);
        Assert.Equal("abcdefghij", terminal.Line(0));
        Assert.Equal(TerminalCell.Blank, terminal.Cell(0, 10));
        Assert.Equal(TerminalCell.Blank, terminal.Cell(0, 11));
    }

    [Fact]
    public void TruncatingAWideGlyphDoesNotLeaveAHiddenContinuation()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("ab中");

        terminal.Emulator.Resize(3, 3);
        terminal.Emulator.Resize(10, 3);

        AssertValidWidePairs(terminal);
        Assert.Equal("ab中", terminal.Line(0));
    }

    [Fact]
    public void PartialWideningDoesNotExposeHalfAWideGlyph()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("abcdef中ij");

        terminal.Emulator.Resize(5, 3);
        terminal.Emulator.Resize(7, 3);

        Assert.Equal("abcdef", terminal.Line(0));
        Assert.NotEqual(CellWidth.Wide, terminal.Cell(0, 6).Width);
        Assert.Empty(terminal.Emulator.FindText("中"));

        terminal.Emulator.Resize(10, 3);
        Assert.Equal("abcdef中ij", terminal.Line(0));
        Assert.Single(terminal.Emulator.FindText("中"));
    }

    [Fact]
    public void ErasingTheVisibleTailInvalidatesHiddenColumns()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("abcdefghij");

        terminal.Emulator.Resize(5, 3);
        terminal.Feed("\u001b[1;1H\u001b[0K");
        terminal.Emulator.Resize(10, 3);

        Assert.Equal(string.Empty, terminal.Line(0));
    }

    [Fact]
    public void ErasingWholeDisplayAfterNarrowingKeepsEraseBackgroundAfterEdit()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("abcdefghij\r\nklmnopqrst");
        terminal.Emulator.Resize(5, 3);

        // Row 1 was narrowed and is not the cursor row, so EraseInDisplay clears
        // it without a preceding PrepareForEdit. That full-row clear must reset
        // the invalidation flag: a later edit keeps the erase background.
        terminal.Feed("\u001b[1;1H\u001b[44m\u001b[2J");
        terminal.Feed("\u001b[2;1H\u001b[0mX");
        terminal.Emulator.Resize(10, 3);

        Assert.Equal("X", terminal.Line(1));
        Assert.Equal(TerminalColor.FromIndex(4), terminal.Cell(1, 9).Background);
    }

    [Fact]
    public void FillScreenAfterNarrowingKeepsFillWhenWideningBack()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("abcdefghij");
        terminal.Emulator.Resize(5, 3);

        terminal.Feed("\u001b#8");
        terminal.Feed("\u001b[1;1HX");
        terminal.Emulator.Resize(10, 3);

        // The alignment pattern overwrote the whole backing row; the edit of
        // the first cell must not blank the hidden remainder.
        Assert.Equal("XEEEEEEEEE", terminal.Line(0));
    }

    [Fact]
    public void EditingAfterNarrowingClearsPartiallyVisibleWideGlyphBeforePrint()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("ab中efghij");
        terminal.Emulator.Resize(3, 3);

        terminal.Feed("\u001b[1;1HX");

        AssertValidWidePairs(terminal);
        terminal.Emulator.Resize(10, 3);
        Assert.Equal("Xb", terminal.Line(0));
        AssertValidWidePairs(terminal);
    }

    [Fact]
    public void HeightResizeAfterNarrowingStillInvalidatesHiddenTextOnEdit()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("abcdefghij");

        terminal.Emulator.Resize(5, 3);
        // A height-only resize reuses the row, so it must not discard the
        // pending hidden-tail invalidation.
        terminal.Emulator.Resize(5, 4);
        terminal.Feed("\u001b[1;1HX");
        terminal.Emulator.Resize(10, 3);

        Assert.Equal("Xbcde", terminal.Line(0));
    }

    [Fact]
    public void DeletingAfterNarrowingClearsPartiallyVisibleWideGlyph()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("ab中efghij");
        terminal.Emulator.Resize(3, 3);

        terminal.Feed("\u001b[1;1H\u001b[P");

        AssertValidWidePairs(terminal);
        terminal.Emulator.Resize(10, 3);
        Assert.Equal("b", terminal.Line(0));
        AssertValidWidePairs(terminal);
    }

    [Fact]
    public void InsertingOnANarrowActiveRowDoesNotRestoreHiddenText()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("abcdefghij");

        terminal.Emulator.Resize(5, 3);
        terminal.Feed("\u001b[1;1H\u001b[2@");
        terminal.Emulator.Resize(10, 3);

        Assert.Equal("  abc", terminal.Line(0));
    }

    [Fact]
    public void DeletingOnANarrowActiveRowDoesNotRestoreHiddenText()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("abcdefghij");

        terminal.Emulator.Resize(5, 3);
        terminal.Feed("\u001b[1;1H\u001b[2P");
        terminal.Emulator.Resize(10, 3);

        Assert.Equal("cde", terminal.Line(0));
    }

    [Fact]
    public void NarrowingHistoryPreservesWideGlyphContent()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("ab中efghij");

        terminal.Emulator.Resize(3, 3);
        terminal.Emulator.Resize(10, 3);

        Assert.Equal("ab中efghij", terminal.Line(0));
        AssertValidWidePairs(terminal);
    }

    [Fact]
    public void NarrowingTheWindowDoesNotDiscardScrollbackContent()
    {
        var terminal = new TestTerminal(10, 2);
        terminal.Feed("abcdefghij\r\nx\r\ny");
        Assert.Equal(1, terminal.Emulator.ScrollbackLength);

        terminal.Emulator.Resize(5, 2);
        terminal.Emulator.Resize(10, 2);
        terminal.Emulator.ScrollViewport(1);

        Assert.Equal("abcdefghij", terminal.Line(0));
    }

    [Fact]
    public void WideGlyphHistorySurvivesNarrowingAndWidening()
    {
        var terminal = new TestTerminal(10, 2);
        terminal.Feed("ab中efghij\r\nx\r\ny");
        Assert.Equal(1, terminal.Emulator.ScrollbackLength);

        terminal.Emulator.Resize(3, 2);
        terminal.Emulator.Resize(10, 2);
        terminal.Emulator.ScrollViewport(1);

        Assert.Equal("ab中efghij", terminal.Line(0));
        AssertValidWidePairs(terminal);
    }

    [Fact]
    public void WideGlyphsSurviveNarrowingAndWidening()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("中文");

        terminal.Emulator.Resize(4, 3);
        AssertValidWidePairs(terminal);
        terminal.Emulator.Resize(10, 3);

        Assert.Equal("中文", terminal.Line(0));
        AssertValidWidePairs(terminal);
    }

    private static void AssertValidWidePairs(TestTerminal terminal)
    {
        for (int column = 0; column < terminal.Columns; column++)
        {
            TerminalCell cell = terminal.Cell(0, column);
            if (cell.Width == CellWidth.Wide)
            {
                Assert.True(column + 1 < terminal.Columns, "a wide glyph must have a continuation cell");
                Assert.Equal(CellWidth.Continuation, terminal.Cell(0, column + 1).Width);
            }
            else if (cell.Width == CellWidth.Continuation)
            {
                Assert.True(column > 0, "a continuation cell must follow a wide glyph");
                Assert.Equal(CellWidth.Wide, terminal.Cell(0, column - 1).Width);
            }
        }
    }
}
