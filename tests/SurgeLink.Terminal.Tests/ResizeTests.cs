namespace SurgeLink.Terminal.Tests;

public sealed class ResizeTests
{
    [Fact]
    public void GrowingColumnsPadsWithBlanks()
    {
        var terminal = new TestTerminal(5, 3);
        terminal.Feed("abc");
        terminal.Emulator.Resize(10, 3);
        Assert.Equal(10, terminal.Emulator.Columns);
        Assert.Equal("abc", terminal.Line(0));
        Assert.Equal(TerminalCell.Blank, terminal.Cell(0, 5));
    }

    [Fact]
    public void ShrinkingColumnsTruncatesContent()
    {
        var terminal = new TestTerminal(10, 3);
        terminal.Feed("abcdefghij");
        terminal.Emulator.Resize(5, 3);
        Assert.Equal(5, terminal.Emulator.Columns);
        Assert.Equal("abcde", terminal.Line(0));
    }

    [Fact]
    public void GrowingRowsAddsBlankRows()
    {
        var terminal = new TestTerminal(5, 3);
        terminal.Feed("hi");
        terminal.Emulator.Resize(5, 5);
        Assert.Equal(5, terminal.Emulator.Rows);
        Assert.Equal("hi", terminal.Line(0));
        Assert.Equal(5, terminal.Lines.Length);
        Assert.Equal(string.Empty, terminal.Line(4));
    }

    [Fact]
    public void ShrinkingRowsKeepsTopContent()
    {
        var terminal = new TestTerminal(10, 5);
        terminal.Feed("1\r\n2\r\n3\r\n4\r\n5");
        terminal.Emulator.Resize(10, 2);
        Assert.Equal(2, terminal.Emulator.Rows);
        Assert.Equal("1", terminal.Line(0));
        Assert.Equal("2", terminal.Line(1));
    }

    [Fact]
    public void CursorIsClampedOnResize()
    {
        var terminal = new TestTerminal(10, 5);
        terminal.Feed("\u001b[5;9H");
        terminal.Emulator.Resize(5, 2);
        Assert.Equal(1, terminal.Cursor.Row);
        Assert.Equal(4, terminal.Cursor.Column);
    }

    [Fact]
    public void TruncationClearsDanglingWideHalf()
    {
        var terminal = new TestTerminal(5, 2);
        terminal.Feed("ab中");
        terminal.Emulator.Resize(3, 2);
        Assert.Equal("ab", terminal.Line(0));
        Assert.Equal(CellWidth.Narrow, terminal.Cell(0, 2).Width);
    }

    [Fact]
    public void ResizeResetsScrollRegion()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[1;1H0\u001b[2;1H1\u001b[3;1H2\u001b[4;1H3\u001b[5;1H4");
        terminal.Feed("\u001b[2;4r");
        terminal.Emulator.Resize(10, 5);
        terminal.Feed("\u001b[5;1H\n");
        Assert.Equal("1", terminal.Line(0));
        Assert.Equal("2", terminal.Line(1));
        Assert.Equal("4", terminal.Line(3));
    }

    [Fact]
    public void ResizeRaisesInvalidatedOnce()
    {
        var terminal = new TestTerminal(10, 5);
        int raised = 0;
        terminal.Emulator.Invalidated += (_, _) => raised++;
        terminal.Emulator.Resize(20, 8);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void InvalidSizesThrow()
    {
        var terminal = new TestTerminal(10, 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => terminal.Emulator.Resize(0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => terminal.Emulator.Resize(10, 0));
    }

    [Fact]
    public void ResizeScalesScrollbackLines()
    {
        var terminal = new TestTerminal(10, 2);
        terminal.Feed("1\r\n2\r\n3");
        Assert.Equal(1, terminal.Emulator.ScrollbackLength);
        terminal.Emulator.Resize(5, 2);
        terminal.Emulator.ScrollViewport(1);
        Assert.Equal("1", terminal.Line(0));
    }

    [Fact]
    public void VersionIncreasesOnResize()
    {
        var terminal = new TestTerminal(10, 5);
        long before = terminal.Emulator.Version;
        terminal.Emulator.Resize(12, 6);
        Assert.True(terminal.Emulator.Version > before);
    }
}
