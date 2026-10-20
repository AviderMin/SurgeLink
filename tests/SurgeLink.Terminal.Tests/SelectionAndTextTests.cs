namespace SurgeLink.Terminal.Tests;

public sealed class SelectionAndTextTests
{
    private static TestTerminal Create()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("hello   \r\nsecond line\u001b[4;1Hthird");
        return terminal;
    }

    private static string Select(TestTerminal terminal, int startRow, int startColumn, int endRow, int endColumn)
    {
        var selection = new TerminalSelection(
            new TerminalPosition(startRow, startColumn),
            new TerminalPosition(endRow, endColumn));
        return terminal.Emulator.GetSelectionText(selection);
    }

    [Fact]
    public void VisibleTextTrimsTrailingBlanksPerLine()
    {
        TestTerminal terminal = Create();
        Assert.Equal("hello", terminal.Line(0));
        Assert.Equal("second line", terminal.Line(1));
        Assert.Equal("third", terminal.Line(3));
    }

    [Fact]
    public void SelectionWithinOneLine()
    {
        TestTerminal terminal = Create();
        Assert.Equal("ell", Select(terminal, 0, 1, 0, 3));
    }

    [Fact]
    public void SelectionAnchorsAreNormalised()
    {
        TestTerminal terminal = Create();
        var selection = TerminalSelection.FromAnchors(new TerminalPosition(0, 3), new TerminalPosition(0, 1));
        Assert.Equal("ell", terminal.Emulator.GetSelectionText(selection));
    }

    [Fact]
    public void MultiLineSelectionJoinsWithNewLines()
    {
        TestTerminal terminal = Create();
        Assert.Equal("lo\nsec", Select(terminal, 0, 3, 1, 2));
    }

    [Fact]
    public void SelectionTrimsTrailingBlanks()
    {
        TestTerminal terminal = Create();
        Assert.Equal("hello", Select(terminal, 0, 0, 0, 19));
    }

    [Fact]
    public void SingleCellSelectionReturnsCellText()
    {
        TestTerminal terminal = Create();
        Assert.Equal("h", Select(terminal, 0, 0, 0, 0));
    }

    [Fact]
    public void SelectionOutsideTheScreenIsClamped()
    {
        TestTerminal terminal = Create();
        Assert.Equal("hello\nsecond line\n\nthird\n", Select(terminal, 0, 0, 99, 99));
    }

    [Fact]
    public void EmptyAreaSelectionReturnsEmptyString()
    {
        TestTerminal terminal = Create();
        Assert.Equal(string.Empty, Select(terminal, 2, 5, 2, 8));
    }

    [Fact]
    public void SelectionReadsScrollbackThroughViewport()
    {
        var terminal = new TestTerminal(10, 2);
        terminal.Feed("1\r\n2\r\n3");
        terminal.Emulator.ScrollViewport(1);
        Assert.Equal("1", terminal.Line(0));
        Assert.Equal("2", terminal.Line(1));
        Assert.Equal("1\n2", Select(terminal, 0, 0, 1, 9));
    }

    [Fact]
    public void VisibleTextUsesCurrentViewport()
    {
        var terminal = new TestTerminal(10, 2);
        terminal.Feed("1\r\n2\r\n3");
        terminal.Emulator.ScrollViewport(1);
        Assert.Equal("1", terminal.Lines[0]);
        Assert.Equal("2", terminal.Lines[1]);
    }
}
