namespace SurgeLink.Terminal.Tests;

public sealed class AlternateScreenTests
{
    [Fact]
    public void Mode1049SwapsBuffersAndRestoresPrimary()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("hello");
        terminal.Feed("\u001b[?1049h");
        Assert.True(terminal.Modes.AlternateScreen);
        Assert.Equal(string.Empty, terminal.Line(0));
        terminal.Feed("\u001b[HALT");
        Assert.Equal("ALT", terminal.Line(0));
        terminal.Feed("\u001b[?1049l");
        Assert.False(terminal.Modes.AlternateScreen);
        Assert.Equal("hello", terminal.Line(0));
        Assert.Equal(0, terminal.Cursor.Row);
        Assert.Equal(5, terminal.Cursor.Column);
    }

    [Fact]
    public void Mode1049ClearsAlternateScreenOnEveryEntry()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[?1049h\u001b[Hone\u001b[?1049l");
        terminal.Feed("\u001b[?1049h");
        Assert.Equal(string.Empty, terminal.Line(0));
    }

    [Fact]
    public void Mode1049KeepsPrimaryScrollback()
    {
        var terminal = new TestTerminal(10, 2);
        terminal.Feed("1\r\n2\r\n3");
        int scrollback = terminal.Emulator.ScrollbackLength;
        Assert.Equal(1, scrollback);
        terminal.Feed("\u001b[?1049h");
        terminal.Feed("a\r\nb\r\nc\r\nd");
        Assert.Equal(scrollback, terminal.Emulator.ScrollbackLength);
        terminal.Feed("\u001b[?1049l");
        Assert.Equal(scrollback, terminal.Emulator.ScrollbackLength);
        Assert.Equal("2", terminal.Line(0));
        Assert.Equal("3", terminal.Line(1));
    }

    [Fact]
    public void Mode47KeepsAlternateContentAcrossRoundTrips()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("P");
        terminal.Feed("\u001b[?47h\u001b[Hone\u001b[?47l");
        Assert.False(terminal.Modes.AlternateScreen);
        Assert.Equal("P", terminal.Line(0));
        terminal.Feed("\u001b[?47h");
        Assert.True(terminal.Modes.AlternateScreen);
        Assert.Equal("one", terminal.Line(0));
    }

    [Fact]
    public void Mode1047ClearsAlternateScreenOnExit()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("P");
        terminal.Feed("\u001b[?1047h\u001b[Hone\u001b[?1047l");
        Assert.Equal("P", terminal.Line(0));
        terminal.Feed("\u001b[?1047h");
        Assert.Equal(string.Empty, terminal.Line(0));
    }

    [Fact]
    public void Mode1048OnlySavesAndRestoresCursor()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[3;4H\u001b[?1048h\u001b[1;1H\u001b[?1048l");
        Assert.False(terminal.Modes.AlternateScreen);
        Assert.Equal(2, terminal.Cursor.Row);
        Assert.Equal(3, terminal.Cursor.Column);
    }

    [Fact]
    public void Mode1049RestoresPrimaryPenAndOriginMode()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b[31m\u001b[3;4H");
        terminal.Feed("\u001b[?1049h\u001b[42m\u001b[?6h");
        Assert.True(terminal.Modes.OriginMode);
        terminal.Feed("\u001b[?1049l");

        Assert.False(terminal.Modes.OriginMode);
        // The saved cursor returns to 3;4 and keeps the primary red pen; the
        // application's green background must not leak into the primary screen.
        terminal.Feed("X");
        Assert.Equal(TerminalColor.FromIndex(1), terminal.Cell(2, 3).Foreground);
        Assert.Equal(TerminalColor.Default, terminal.Cell(2, 3).Background);
    }

    [Fact]
    public void AlternateScreenIgnoresScrollViewport()
    {
        var terminal = new TestTerminal(10, 2);
        terminal.Feed("1\r\n2\r\n3");
        terminal.Feed("\u001b[?1049h");
        terminal.Feed("\u001b[2;1H");
        terminal.Emulator.ScrollViewport(1);
        Assert.Equal(0, terminal.Emulator.ViewportOffset);
    }
}
