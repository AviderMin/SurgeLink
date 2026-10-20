namespace SurgeLink.Terminal.Tests;

public sealed class EraseAndEditTests
{
    [Fact]
    public void EraseLineToEnd()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("abcdef\r\u001b[3C\u001b[K");
        Assert.Equal("abc", terminal.Line(0));
    }

    [Fact]
    public void EraseLineToStart()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("abcdef\r\u001b[3C\u001b[1K");
        Assert.Equal("    ef", terminal.Line(0));
    }

    [Fact]
    public void EraseWholeLine()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("abcdef\u001b[2K");
        Assert.Equal(string.Empty, terminal.Line(0));
        Assert.Equal(6, terminal.Cursor.Column);
    }

    [Fact]
    public void EraseDisplayToEnd()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("abc\r\nXYZ\u001b[1;2H\u001b[J");
        Assert.Equal("a", terminal.Line(0));
        Assert.Equal(string.Empty, terminal.Line(1));
    }

    [Fact]
    public void EraseDisplayToStart()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("abc\r\nXYZ\u001b[2;2H\u001b[1J");
        Assert.Equal(string.Empty, terminal.Line(0));
        Assert.Equal("  Z", terminal.Line(1));
    }

    [Fact]
    public void EraseWholeDisplay()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("one\r\ntwo\r\nthree\u001b[2J");
        Assert.All(terminal.Lines, line => Assert.Equal(string.Empty, line));
    }

    [Fact]
    public void EraseScrollbackWithDisplayModeThree()
    {
        var terminal = new TestTerminal(10, 2);
        terminal.Feed("1\r\n2\r\n3");
        Assert.Equal(1, terminal.Emulator.ScrollbackLength);
        terminal.Feed("\u001b[3J");
        Assert.Equal(0, terminal.Emulator.ScrollbackLength);
        Assert.All(terminal.Lines, line => Assert.Equal(string.Empty, line));
    }

    [Fact]
    public void EraseCharactersBlanksRun()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("abcdef\r\u001b[2C\u001b[2X");
        Assert.Equal("ab  ef", terminal.Line(0));
    }

    [Fact]
    public void InsertCharactersShiftsRight()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("abcdef\r\u001b[2C\u001b[2@");
        Assert.Equal("ab  cdef", terminal.Line(0));
    }

    [Fact]
    public void DeleteCharactersShiftsLeft()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("abcdef\r\u001b[2C\u001b[2P");
        Assert.Equal("abef", terminal.Line(0));
    }

    [Fact]
    public void InsertLinesShiftsContentDown()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("1\r\n2\r\n3\r\n4");
        terminal.Feed("\u001b[2;1H\u001b[1L");
        Assert.Equal("1", terminal.Line(0));
        Assert.Equal(string.Empty, terminal.Line(1));
        Assert.Equal("2", terminal.Line(2));
        Assert.Equal("3", terminal.Line(3));
    }

    [Fact]
    public void DeleteLinesShiftsContentUp()
    {
        var terminal = new TestTerminal(10, 4);
        terminal.Feed("1\r\n2\r\n3\r\n4");
        terminal.Feed("\u001b[2;1H\u001b[1M");
        Assert.Equal("1", terminal.Line(0));
        Assert.Equal("3", terminal.Line(1));
        Assert.Equal("4", terminal.Line(2));
        Assert.Equal(string.Empty, terminal.Line(3));
    }

    [Fact]
    public void InsertMultipleLines()
    {
        var terminal = new TestTerminal(10, 5);
        terminal.Feed("1\r\n2\r\n3\r\n4\r\n5");
        terminal.Feed("\u001b[2;1H\u001b[2L");
        Assert.Equal("1", terminal.Line(0));
        Assert.Equal(string.Empty, terminal.Line(1));
        Assert.Equal(string.Empty, terminal.Line(2));
        Assert.Equal("2", terminal.Line(3));
        Assert.Equal("3", terminal.Line(4));
    }

    [Fact]
    public void DeleteMultipleLines()
    {
        var terminal = new TestTerminal(10, 5);
        terminal.Feed("1\r\n2\r\n3\r\n4\r\n5");
        terminal.Feed("\u001b[2;1H\u001b[2M");
        Assert.Equal("1", terminal.Line(0));
        Assert.Equal("4", terminal.Line(1));
        Assert.Equal("5", terminal.Line(2));
        Assert.Equal(string.Empty, terminal.Line(3));
        Assert.Equal(string.Empty, terminal.Line(4));
    }
}
