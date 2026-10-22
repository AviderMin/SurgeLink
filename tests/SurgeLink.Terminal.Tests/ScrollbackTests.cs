namespace SurgeLink.Terminal.Tests;

public sealed class ScrollbackTests
{
    private static TestTerminal WithHistory()
    {
        var terminal = new TestTerminal(10, 3, scrollbackLimit: 5);
        terminal.Feed("1\r\n2\r\n3\r\n4\r\n5");
        return terminal;
    }

    [Theory]
    [InlineData(0, -2, 2)]
    [InlineData(1, -1, 3)]
    [InlineData(2, 0, 4)]
    public void SelectionCanCopyHistoryAndLiveRowsAcrossViewport(int offset, int first, int last)
    {
        var terminal = WithHistory();
        terminal.Emulator.ScrollViewport(offset);
        var selection = new TerminalSelection(new TerminalPosition(first, 0), new TerminalPosition(last, 9));
        Assert.Equal("1\n2\n3\n4\n5", terminal.Emulator.GetSelectionText(selection));
        Assert.Equal("1\n2\n3\n4\n5", terminal.Emulator.GetSelectionText(new TerminalSelection(selection.End, selection.Start)));
    }

    [Fact]
    public void ScrolledOffLinesAreRetained()
    {
        TestTerminal terminal = WithHistory();
        Assert.Equal(2, terminal.Emulator.ScrollbackLength);
        Assert.Equal("3", terminal.Line(0));
        Assert.Equal("4", terminal.Line(1));
        Assert.Equal("5", terminal.Line(2));
    }

    [Fact]
    public void ScrollViewportShowsHistory()
    {
        TestTerminal terminal = WithHistory();
        terminal.Emulator.ScrollViewport(1);
        Assert.Equal(1, terminal.Emulator.ViewportOffset);
        Assert.Equal("2", terminal.Line(0));
        Assert.Equal("3", terminal.Line(1));
        Assert.Equal("4", terminal.Line(2));
    }

    [Fact]
    public void ScrollViewportNegativeReturnsTowardsLiveView()
    {
        TestTerminal terminal = WithHistory();
        terminal.Emulator.ScrollViewport(2);
        Assert.Equal("1", terminal.Line(0));
        terminal.Emulator.ScrollViewport(-1);
        Assert.Equal("2", terminal.Line(0));
    }

    [Fact]
    public void ScrollViewportClampsToAvailableHistory()
    {
        TestTerminal terminal = WithHistory();
        terminal.Emulator.ScrollViewport(100);
        Assert.Equal(2, terminal.Emulator.ViewportOffset);
        terminal.Emulator.ScrollViewport(-100);
        Assert.Equal(0, terminal.Emulator.ViewportOffset);
    }

    [Fact]
    public void ScrollToBottomReturnsToLiveView()
    {
        TestTerminal terminal = WithHistory();
        terminal.Emulator.ScrollViewport(2);
        terminal.Emulator.ScrollToBottom();
        Assert.Equal(0, terminal.Emulator.ViewportOffset);
        Assert.Equal("5", terminal.Line(2));
    }

    [Fact]
    public void NewOutputResetsViewportOffset()
    {
        TestTerminal terminal = WithHistory();
        terminal.Emulator.ScrollViewport(2);
        terminal.Feed("X");
        Assert.Equal(0, terminal.Emulator.ViewportOffset);
        Assert.Equal("5X", terminal.Line(2));
    }

    [Fact]
    public void GetCellReadsScrollbackThroughViewportOffset()
    {
        TestTerminal terminal = WithHistory();
        terminal.Emulator.ScrollViewport(2);
        Assert.Equal("1", terminal.Emulator.GetCell(0, 0).Text);
        Assert.Equal("2", terminal.Emulator.GetCell(1, 0).Text);
    }

    [Fact]
    public void ScrollbackLimitIsEnforced()
    {
        var terminal = new TestTerminal(10, 2, scrollbackLimit: 2);
        terminal.Feed("1\r\n2\r\n3\r\n4\r\n5\r\n");
        Assert.Equal(2, terminal.Emulator.ScrollbackLength);
        terminal.Emulator.ScrollViewport(2);
        Assert.Equal("3", terminal.Line(0));
        Assert.Equal("4", terminal.Line(1));
    }

    [Fact]
    public void ZeroScrollbackLimitKeepsNoHistory()
    {
        var terminal = new TestTerminal(10, 2, scrollbackLimit: 0);
        terminal.Feed("1\r\n2\r\n3\r\n4");
        Assert.Equal(0, terminal.Emulator.ScrollbackLength);
        Assert.Equal(0, terminal.Emulator.ViewportOffset);
        Assert.Equal("4", terminal.Line(1));
    }

    [Fact]
    public void ResetKeepsScrollbackButClearsScreen()
    {
        TestTerminal terminal = WithHistory();
        terminal.Emulator.Reset();
        Assert.Equal(2, terminal.Emulator.ScrollbackLength);
        Assert.All(terminal.Lines, line => Assert.Equal(string.Empty, line));
        Assert.Equal(0, terminal.Emulator.ViewportOffset);
    }

    [Fact]
    public void EdThreeClearsScrollback()
    {
        TestTerminal terminal = WithHistory();
        terminal.Feed("\u001b[3J");
        Assert.Equal(0, terminal.Emulator.ScrollbackLength);
        Assert.Equal(0, terminal.Emulator.ViewportOffset);
    }
}
