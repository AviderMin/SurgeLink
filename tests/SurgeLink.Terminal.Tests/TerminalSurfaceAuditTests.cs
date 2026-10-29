namespace SurgeLink.Terminal.Tests;

/// <summary>
/// Regressions found while auditing the terminal for real user-facing problems:
/// wide glyphs corrupted by cursor/editing commands and the context-menu clear
/// action resetting remote terminal state.
/// </summary>
public sealed class TerminalSurfaceAuditTests
{
    [Fact]
    public void BackTabSkipsAStopCoveredByAWideGlyph()
    {
        var terminal = new TestTerminal(24, 3);

        terminal.Feed("1234567\u4e2d");
        Assert.Equal(CellWidth.Wide, terminal.Cell(0, 7).Width);
        Assert.Equal(CellWidth.Continuation, terminal.Cell(0, 8).Width);

        // Column 8 is the default stop after column 0 but the glyph owns it as its
        // right half. Landing there and printing erased half of "中".
        terminal.Feed("\u001b[1;17H");
        terminal.Feed("\u001b[Z");

        Assert.NotEqual(CellWidth.Continuation, terminal.Cell(0, terminal.Cursor.Column).Width);
        terminal.Feed("X");
        Assert.Equal(CellWidth.Wide, terminal.Cell(0, 7).Width);
        Assert.Equal("\u4e2d", terminal.CellText(0, 7));
    }

    [Fact]
    public void InsertingBeforeAWideGlyphKeepsBothHalvesTogether()
    {
        var terminal = new TestTerminal(10, 3);

        terminal.Feed("\uff21\uff22");
        // Insert one blank cell at column 2, the left half of the second glyph.
        terminal.Feed("\u001b[1;3H\u001b[1@");

        AssertValidWidePairs(terminal);
        Assert.Equal("\uff21", terminal.CellText(0, 0));
        Assert.Equal(CellWidth.Continuation, terminal.Cell(0, 1).Width);
        Assert.Equal(CellWidth.Narrow, terminal.Cell(0, 2).Width);
        Assert.Equal("\uff22", terminal.CellText(0, 3));
        Assert.Equal(CellWidth.Continuation, terminal.Cell(0, 4).Width);
    }

    [Fact]
    public void InsertingInsideAWideGlyphSplitsItCleanly()
    {
        var terminal = new TestTerminal(10, 3);

        terminal.Feed("\uff21\uff22");
        // Insert at column 3, the right half of the second glyph.
        terminal.Feed("\u001b[1;4H\u001b[1@");

        AssertValidWidePairs(terminal);
        Assert.Equal("\uff21", terminal.CellText(0, 0));
        Assert.Equal(CellWidth.Narrow, terminal.Cell(0, 2).Width);
        Assert.Equal(CellWidth.Narrow, terminal.Cell(0, 3).Width);
    }

    [Fact]
    public void ClearDisplayKeepsModesAndAlternateScreen()
    {
        var terminal = new TestTerminal(12, 3);
        terminal.Feed("\u001b[?1049h\u001b[?1000h\u001b[?1006h\u001b[?1h");
        terminal.Feed("hello");

        terminal.Emulator.ClearDisplay();

        Assert.True(terminal.Modes.AlternateScreen);
        Assert.True(terminal.Modes.MouseTracking);
        Assert.True(terminal.Modes.SgrMouseEncoding);
        Assert.True(terminal.Modes.ApplicationCursorKeys);
        Assert.All(terminal.Lines, line => Assert.Equal(string.Empty, line));
        Assert.Equal(0, terminal.Cursor.Row);
        Assert.Equal(0, terminal.Cursor.Column);
    }

    [Fact]
    public void ClearDisplayDropsScrollbackAndUsesDefaultBackground()
    {
        var terminal = new TestTerminal(12, 2, 100);
        terminal.Feed("\u001b[41mone\r\ntwo\r\nthree\r\nfour");
        Assert.True(terminal.Emulator.ScrollbackLength > 0);

        terminal.Emulator.ClearDisplay();

        Assert.Equal(0, terminal.Emulator.ScrollbackLength);
        Assert.Equal(0, terminal.Emulator.ViewportOffset);
        Assert.All(terminal.Lines, line => Assert.Equal(string.Empty, line));
        Assert.Equal(TerminalColor.Default, terminal.Cell(0, 0).Background);
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
