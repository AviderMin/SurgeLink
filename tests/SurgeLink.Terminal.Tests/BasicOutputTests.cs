namespace SurgeLink.Terminal.Tests;

public sealed class BasicOutputTests
{
    [Fact]
    public void PrintsAsciiText()
    {
        var terminal = new TestTerminal();
        terminal.Feed("hello");
        Assert.Equal("hello", terminal.Line(0));
        Assert.Equal(5, terminal.Cursor.Column);
    }

    [Fact]
    public void CarriageReturnNewLineStartsNewRow()
    {
        var terminal = new TestTerminal();
        terminal.Feed("one\r\ntwo");
        Assert.Equal("one", terminal.Line(0));
        Assert.Equal("two", terminal.Line(1));
        Assert.Equal(1, terminal.Cursor.Row);
    }

    [Fact]
    public void LineFeedPreservesColumn()
    {
        var terminal = new TestTerminal();
        terminal.Feed("ab\n");
        Assert.Equal(1, terminal.Cursor.Row);
        Assert.Equal(2, terminal.Cursor.Column);
        terminal.Feed("X");
        Assert.Equal("  X", terminal.Line(1));
    }

    [Fact]
    public void CarriageReturnMovesToColumnZero()
    {
        var terminal = new TestTerminal();
        terminal.Feed("abc\rX");
        Assert.Equal("Xbc", terminal.Line(0));
    }

    [Fact]
    public void BackspaceMovesCursorLeft()
    {
        var terminal = new TestTerminal();
        terminal.Feed("abc\bX");
        Assert.Equal("abX", terminal.Line(0));
    }

    [Fact]
    public void BackspaceAtColumnZeroStaysPut()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\bX");
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void HorizontalTabMovesToNextDefaultStop()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\tX");
        Assert.Equal("        X", terminal.Line(0));
    }

    [Fact]
    public void HorizontalTabAfterTextUsesNextStop()
    {
        var terminal = new TestTerminal();
        terminal.Feed("abc\tX");
        Assert.Equal("abc     X", terminal.Line(0));
    }

    [Fact]
    public void BellAndNullAreIgnored()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\0\aX");
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void DeleteCharacterIsIgnored()
    {
        var terminal = new TestTerminal();
        terminal.Feed("a\u007Fb");
        Assert.Equal("ab", terminal.Line(0));
    }

    [Fact]
    public void VerticalTabAndFormFeedActAsLineFeed()
    {
        var terminal = new TestTerminal();
        terminal.Feed("a\v");
        Assert.Equal(1, terminal.Cursor.Row);
        terminal.Feed("b");
        Assert.Equal("a", terminal.Line(0));
        Assert.Equal(" b", terminal.Line(1));
    }

    [Fact]
    public void ShiftOutAndShiftInAreIgnored()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u000eX\u000fY");
        Assert.Equal("XY", terminal.Line(0));
    }

    [Fact]
    public void DefaultSizeMatchesContract()
    {
        var emulator = new TerminalEmulator();
        Assert.Equal(120, emulator.Columns);
        Assert.Equal(32, emulator.Rows);
        Assert.Equal(10000, emulator.ScrollbackLimit);
    }

    [Fact]
    public void OutOfRangeCellsAreBlank()
    {
        var terminal = new TestTerminal();
        Assert.Equal(TerminalCell.Blank, terminal.Emulator.GetCell(-1, 0));
        Assert.Equal(TerminalCell.Blank, terminal.Emulator.GetCell(0, -1));
        Assert.Equal(TerminalCell.Blank, terminal.Emulator.GetCell(99, 0));
        Assert.Equal(TerminalCell.Blank, terminal.Emulator.GetCell(0, 99));
    }

    [Fact]
    public void VisibleTextAlwaysHasOneLinePerRow()
    {
        var terminal = new TestTerminal(20, 6);
        Assert.Equal(6, terminal.Lines.Length);
        Assert.All(terminal.Lines, line => Assert.Equal(string.Empty, line));
    }

    [Fact]
    public void ConstructorRejectsInvalidSizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TerminalEmulator(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TerminalEmulator(10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TerminalEmulator(10, 10, -1));
    }
}
