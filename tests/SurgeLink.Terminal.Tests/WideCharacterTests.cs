namespace SurgeLink.Terminal.Tests;

public sealed class WideCharacterTests
{
    [Theory]
    [InlineData(0x2630, 2)] // Trigram: widened in modern Unicode.
    [InlineData(0x1FAE9, 2)] // Modern emoji omitted from the old hand-maintained table.
    [InlineData(0x1F321, 1)] // Thermometer is neutral without emoji presentation.
    [InlineData(0x1F203, 1)] // Unassigned gap within the old broad symbol range.
    [InlineData(0xFF00, 1)] // Unassigned; must not inherit a neighbouring fullwidth range.
    public void UnicodeWidthPropertiesDoNotOvergeneralizeNeighbouringSymbols(int codePoint, int width)
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed(char.ConvertFromUtf32(codePoint));
        Assert.Equal(width, terminal.Cursor.Column);
    }
    [Fact]
    public void WideGlyphUsesTwoCells()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("中");
        Assert.Equal("中", terminal.CellText(0, 0));
        Assert.Equal(CellWidth.Wide, terminal.Cell(0, 0).Width);
        Assert.Equal(CellWidth.Continuation, terminal.Cell(0, 1).Width);
        Assert.Equal(string.Empty, terminal.Cell(0, 1).Text);
        Assert.Equal(2, terminal.Cursor.Column);
        Assert.Equal("中", terminal.Line(0));
    }

    [Fact]
    public void OverwritingFirstHalfClearsWholeGlyph()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("中\u001b[1;1HX");
        Assert.Equal("X", terminal.Line(0));
        Assert.Equal(CellWidth.Narrow, terminal.Cell(0, 1).Width);
    }

    [Fact]
    public void OverwritingSecondHalfClearsWholeGlyph()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("中\u001b[1;2HY");
        Assert.Equal(" Y", terminal.Line(0));
        Assert.Equal(TerminalCell.Blank, terminal.Cell(0, 0));
    }

    [Fact]
    public void WideGlyphWrapsAtRightMargin()
    {
        var terminal = new TestTerminal(5, 3);
        terminal.Feed("abcd中");
        Assert.Equal("abcd", terminal.Line(0));
        Assert.Equal("中", terminal.Line(1));
        Assert.Equal(CellWidth.Wide, terminal.Cell(1, 0).Width);
    }

    [Fact]
    public void WideGlyphIsDroppedAtRightMarginWithoutAutoWrap()
    {
        var terminal = new TestTerminal(3, 3);
        terminal.Feed("ab\u001b[?7l中");
        Assert.Equal("ab", terminal.Line(0));
        Assert.NotEqual(CellWidth.Wide, terminal.Cell(0, 2).Width);
    }

    [Fact]
    public void EmojiGlyphIsWide()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("😀");
        Assert.Equal(CellWidth.Wide, terminal.Cell(0, 0).Width);
        Assert.Equal(CellWidth.Continuation, terminal.Cell(0, 1).Width);
    }

    [Fact]
    public void ErasingContinuationCellClearsWholeGlyph()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("中\u001b[1;2H\u001b[1X");
        Assert.Equal(string.Empty, terminal.Line(0));
        Assert.Equal(TerminalCell.Blank, terminal.Cell(0, 0));
    }

    [Fact]
    public void CombiningMarkAppendsToWideGlyph()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("中\u0301");
        Assert.Equal("中\u0301", terminal.CellText(0, 0));
        Assert.Equal(2, terminal.Cursor.Column);
    }

    [Fact]
    public void SelectionSkipsContinuationCells()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("中");
        var selection = new TerminalSelection(new TerminalPosition(0, 0), new TerminalPosition(0, 1));
        Assert.Equal("中", terminal.Emulator.GetSelectionText(selection));
    }

    [Fact]
    public void WideGlyphFitsBeforeLastColumn()
    {
        var terminal = new TestTerminal(4, 3);
        terminal.Feed("ab中");
        Assert.Equal("ab中", terminal.Line(0));
        Assert.True(terminal.Cursor.Column == 3);
    }
}
