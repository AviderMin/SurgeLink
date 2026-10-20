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
    public void EscapeInsideDcsDoesNotTerminateIt()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001bPfoo\u001b");
        terminal.Feed("bar");
        terminal.Feed("\u001b\\X");
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void CancelAbortsDcs()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("\u001bPfoo\u0018X");
        Assert.Equal("X", terminal.Line(0));
    }
}
