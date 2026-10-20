namespace SurgeLink.Terminal.Tests;

public sealed class OscTests
{
    [Fact]
    public void OscZeroSetsWindowTitleWithBel()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b]0;My Title\u0007");
        Assert.Equal("My Title", terminal.Modes.WindowTitle);
    }

    [Fact]
    public void OscTwoSetsWindowTitleWithStringTerminator()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b]2;Second Title\u001b\\");
        Assert.Equal("Second Title", terminal.Modes.WindowTitle);
    }

    [Fact]
    public void OscTitleDefaultsToNull()
    {
        var terminal = new TestTerminal();
        Assert.Null(terminal.Modes.WindowTitle);
    }

    [Fact]
    public void EmptyOscTitleClearsTitle()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b]0;first\u0007");
        terminal.Feed("\u001b]2;\u0007");
        Assert.Equal(string.Empty, terminal.Modes.WindowTitle);
    }

    [Fact]
    public void UnknownOscIsIgnoredWithoutEatingFollowingText()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b]9;ignored\u0007X");
        Assert.Equal("X", terminal.Line(0));
        Assert.Null(terminal.Modes.WindowTitle);
    }

    [Fact]
    public void HyperlinkOscIsIgnored()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b]8;;https://example.com\u0007link");
        Assert.Equal("link", terminal.Line(0));
        Assert.Null(terminal.Modes.WindowTitle);
    }

    [Fact]
    public void IconTitleOscIsIgnored()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b]1;icon\u0007");
        Assert.Null(terminal.Modes.WindowTitle);
    }

    [Fact]
    public void ControlCharactersAreStrippedFromTitle()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b]0;a\u0001b\u0002c\u0007");
        Assert.Equal("abc", terminal.Modes.WindowTitle);
    }

    [Fact]
    public void WindowTitleIsCappedAt256Characters()
    {
        var terminal = new TestTerminal();
        string title = new string('a', 300);
        terminal.Feed("\u001b]0;" + title + "\u0007");
        Assert.Equal(256, terminal.Modes.WindowTitle!.Length);
    }

    [Fact]
    public void OscSplitAcrossFeedsIsReassembled()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b]0;Hel");
        terminal.Feed("lo\u0007");
        Assert.Equal("Hello", terminal.Modes.WindowTitle);
    }

    [Fact]
    public void OscDoesNotWriteToTheScreen()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b]0;title\u0007");
        Assert.Equal(string.Empty, terminal.Line(0));
        Assert.Equal(0, terminal.Cursor.Column);
    }
}
