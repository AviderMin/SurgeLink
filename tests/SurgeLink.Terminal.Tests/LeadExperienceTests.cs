using System.Text;

namespace SurgeLink.Terminal.Tests;

public sealed class LeadExperienceTests
{
    [Fact]
    public void NewOutputDoesNotInterruptReadingHistory()
    {
        var terminal = new TerminalEmulator(20, 3, 20);
        Feed(terminal, "1\r\n2\r\n3\r\n4\r\n5");
        terminal.ScrollViewport(2);

        Feed(terminal, "\r\n6");

        Assert.Equal(2, terminal.ViewportOffset);
        Assert.Equal("2\n3\n4", terminal.GetVisibleText());
    }

    [Fact]
    public void OutputWithoutScrollingDoesNotInterruptReadingHistory()
    {
        var terminal = new TerminalEmulator(20, 3, 20);
        Feed(terminal, "1\r\n2\r\n3\r\n4\r\n5");
        terminal.ScrollViewport(2);

        Feed(terminal, "X");

        Assert.Equal(2, terminal.ViewportOffset);
        Assert.Equal("1\n2\n3", terminal.GetVisibleText());
    }

    [Fact]
    public void ExplicitScrollToBottomResumesFollowingOutput()
    {
        var terminal = new TerminalEmulator(20, 3, 20);
        Feed(terminal, "1\r\n2\r\n3\r\n4\r\n5");
        terminal.ScrollViewport(2);
        Feed(terminal, "\r\n6");
        terminal.ScrollToBottom();

        Feed(terminal, "\r\n7");

        Assert.Equal(0, terminal.ViewportOffset);
        Assert.Equal("5\n6\n7", terminal.GetVisibleText());
    }

    [Fact]
    public void BoundedHistoryKeepsReadingPositionWhenOldestLineIsEvicted()
    {
        var terminal = new TerminalEmulator(20, 2, 2);
        Feed(terminal, "1\r\n2\r\n3\r\n4\r\n5\r\n6");
        terminal.ScrollViewport(2);
        Assert.Equal("3\n4", terminal.GetVisibleText());

        Feed(terminal, "\r\n7");

        Assert.Equal(2, terminal.ScrollbackLength);
        Assert.Equal(1, terminal.ViewportOffset);
        // The original first line was evicted; keep the same surviving line visible.
        Assert.Equal("5\n6", terminal.GetVisibleText());
    }

    private static void Feed(TerminalEmulator terminal, string text) =>
        terminal.Feed(Encoding.UTF8.GetBytes(text));
}
