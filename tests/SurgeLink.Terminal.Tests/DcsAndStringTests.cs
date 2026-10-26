namespace SurgeLink.Terminal.Tests;

public sealed class DcsAndStringTests
{
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
