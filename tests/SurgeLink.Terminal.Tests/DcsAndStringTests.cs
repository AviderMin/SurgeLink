namespace SurgeLink.Terminal.Tests;

public sealed class DcsAndStringTests
{
    [Fact]
    public void XtgettcapRespondsToSplitTrueColorQueriesWithoutWritingOnScreen()
    {
        var terminal = new TestTerminal();
        terminal.Emulator.TerminalType = "xterm-direct";
        var responses = new List<string>();
        terminal.Emulator.ResponseRequested += responses.Add;
        terminal.Feed("\u001bP+q524");
        terminal.Feed("742;5463;544e;436f\u001b");
        Assert.Empty(responses);
        terminal.Feed("\\X");

        Assert.Equal(
            [
                "\u001bP1+r524742=38\u001b\\",
                "\u001bP1+r5463=31\u001b\\",
                "\u001bP1+r544E=787465726D2D646972656374\u001b\\",
                "\u001bP1+r436F=323536\u001b\\",
            ],
            responses);
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void XtgettcapHandlesUnsupportedCancelledAndOversizedQueries()
    {
        var terminal = new TestTerminal();
        var responses = new List<string>();
        terminal.Emulator.ResponseRequested += responses.Add;
        terminal.Feed("\u001bP+qFFFF;524742\u001b\\");
        terminal.Feed("\u001bP+q524742\u0018");
        terminal.Feed("\u001bP+q" + new string('0', 300) + "\u001b\\");
        Assert.Equal(
            [
                "\u001bP0+rFFFF\u001b\\",
                "\u001bP1+r524742=38\u001b\\",
            ],
            responses);
    }

    [Fact]
    public void DcsPayloadIsConsumedWithoutScreenChange()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("keep");
        terminal.Feed("\u001bP1;2;3qpayload\u001b\\");
        Assert.Equal("keep", terminal.Line(0));
        terminal.Feed("X");
        Assert.Equal("keepX", terminal.Line(0));
    }

    [Fact]
    public void ApcPayloadIsConsumed()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b_some apc\u001b\\X");
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void PmPayloadIsConsumed()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001b^private message\u001b\\X");
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void SosPayloadIsConsumed()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001bXstart of string\u001b\\X");
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void DcsSplitAcrossFeedsIsConsumed()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001bPq");
        terminal.Feed("some data");
        terminal.Feed("\u001b\\X");
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void NonTerminatingEscapeInsideDcsAbortsTheString()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001bPfoo\u001b");
        // ESC b is not ST: the string ends and the remaining bytes are ordinary
        // output (ESC b itself is a complete escape sequence, so only "ar" prints).
        terminal.Feed("bar\u001b[0m");
        Assert.Equal("ar", terminal.Line(0));
    }

    [Fact]
    public void DcsTerminatorStillWorksAfterAbortedString()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001bPfoo\u001b[31mR\u001b[0m\u001b\\X");

        Assert.Equal("RX", terminal.Line(0));
        Assert.Equal(TerminalColor.FromIndex(1), terminal.Cell(0, 0).Foreground);
    }

    [Theory]
    [InlineData("\u001bP")]
    [InlineData("\u001bX")]
    [InlineData("\u001b^")]
    [InlineData("\u001b_")]
    public void NonTerminatingEscapeInStringAbortsItAndDispatchesFollowingText(string introducer)
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed(introducer + "data\u001b[31mR\u001b[0m");

        Assert.Equal("R", terminal.Line(0));
        Assert.Equal(TerminalColor.FromIndex(1), terminal.Cell(0, 0).Foreground);
    }

    [Fact]
    public void NonTerminatingEscapeInStringIsHandledAcrossSplitFeeds()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001bPdata\u001b");
        terminal.Feed("[32mG\u001b[0m");

        Assert.Equal("G", terminal.Line(0));
        Assert.Equal(TerminalColor.FromIndex(2), terminal.Cell(0, 0).Foreground);
    }

    [Fact]
    public void CancelAbortsDcs()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001bPfoo\u0018X");
        Assert.Equal("X", terminal.Line(0));
    }
}
