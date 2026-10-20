using System.Text;

namespace SurgeLink.Terminal.Tests;

public sealed class RegressionTests
{
    private const string MixedScript = "abc\u001b[31mDEF\u001b[0m\r\n中😀\tX\u001b[2;2Hq";

    [Fact]
    public void EveryChunkSplitProducesTheSameScreen()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(MixedScript);
        var whole = new TestTerminal(20, 5);
        whole.Feed(bytes);
        string expected = whole.Emulator.GetVisibleText();

        for (int split = 0; split <= bytes.Length; split++)
        {
            var chunked = new TestTerminal(20, 5);
            chunked.Feed(bytes.AsSpan(0, split));
            chunked.Feed(bytes.AsSpan(split));
            Assert.Equal(expected, chunked.Emulator.GetVisibleText());
        }
    }

    [Fact]
    public void ByteAtATimeFeedMatchesSingleFeed()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(MixedScript);
        var whole = new TestTerminal(20, 5);
        whole.Feed(bytes);
        var chunked = new TestTerminal(20, 5);
        foreach (byte value in bytes)
        {
            chunked.Feed(value);
        }

        Assert.Equal(whole.Emulator.GetVisibleText(), chunked.Emulator.GetVisibleText());
    }

    [Fact]
    public void CsiSplitAcrossChunksIsParsed()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b[");
        terminal.Feed("31");
        terminal.Feed("mX");
        Assert.Equal(TerminalColor.FromIndex(1), terminal.Cell(0, 0).Foreground);
    }

    [Fact]
    public void MalformedCsiDoesNotBreakFollowingText()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b[?mX");
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void HugeParametersAreClamped()
    {
        var terminal = new TestTerminal(10, 5);
        terminal.Feed("\u001b[999999999999999999;999999999999999999HX");
        Assert.Equal("         X", terminal.Line(4));
    }

    [Fact]
    public void TooManyParametersIgnoreTheSequence()
    {
        var terminal = new TestTerminal(10, 5);
        string parameters = string.Join(';', Enumerable.Repeat("1", 40));
        terminal.Feed("\u001b[" + parameters + "mX");
        Assert.Equal("X", terminal.Line(0));
        Assert.Equal(CellAttributes.None, terminal.Cell(0, 0).Attributes);
    }

    [Fact]
    public void UnknownCsiFinalIsIgnored()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b[5qX");
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void UnknownPrivateAndAnsiModesAreIgnored()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b[?9999h\u001b[99hX");
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void DecPrivateModeQueryIsIgnored()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b[?25$pX");
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void CancelByteAbortsCsi()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b[3\u0018X");
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void SubstituteByteAbortsCsi()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b[3\u001aX");
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void EscapeRestartsAnAbandonedSequence()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b[3\u001b[1;1HX");
        Assert.Equal("X", terminal.Line(0));
    }

    [Fact]
    public void PendingWrapIsCancelledByCarriageReturn()
    {
        var terminal = new TestTerminal(5, 3);
        terminal.Feed("abcde\rX");
        Assert.Equal("Xbcde", terminal.Line(0));
        Assert.Equal(0, terminal.Cursor.Row);
    }

    [Fact]
    public void PendingWrapIsCancelledByCursorPosition()
    {
        var terminal = new TestTerminal(5, 3);
        terminal.Feed("abcde\u001b[1;1HX");
        Assert.Equal("Xbcde", terminal.Line(0));
    }

    [Fact]
    public void LineFeedCancelsPendingWrapButKeepsColumn()
    {
        var terminal = new TestTerminal(5, 3);
        terminal.Feed("abcde\nX");
        Assert.Equal("abcde", terminal.Line(0));
        Assert.Equal("    X", terminal.Line(1));
    }

    [Fact]
    public void InvalidUtf8NeverThrows()
    {
        var terminal = new TestTerminal();
        var bytes = new byte[4096];
        new Random(12345).NextBytes(bytes);
        terminal.Feed(bytes);
        Assert.NotNull(terminal.Emulator.GetVisibleText());
    }

    [Fact]
    public void AllHighBytesAreHandled()
    {
        var terminal = new TestTerminal();
        var bytes = new byte[128];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(0x80 + i);
        }

        terminal.Feed(bytes);
        Assert.NotNull(terminal.Emulator.GetVisibleText());
    }

    [Fact]
    public void VeryLongOscDoesNotThrow()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b]0;" + new string('x', 5000) + "\u0007");
        Assert.Equal(256, terminal.Modes.WindowTitle!.Length);
    }

    [Fact]
    public void VersionIncreasesOnEveryFeed()
    {
        var terminal = new TestTerminal();
        long before = terminal.Emulator.Version;
        terminal.Feed("hello");
        Assert.True(terminal.Emulator.Version > before);
    }

    [Fact]
    public void InvalidatedFiresOncePerFeed()
    {
        var terminal = new TestTerminal();
        int raised = 0;
        terminal.Emulator.Invalidated += (_, _) => raised++;
        terminal.Feed("\u001b[31mhello\r\nworld\u001b[0m");
        Assert.Equal(1, raised);
    }

    [Fact]
    public void InvalidatedFiresOncePerReset()
    {
        var terminal = new TestTerminal();
        terminal.Feed("hello");
        int raised = 0;
        terminal.Emulator.Invalidated += (_, _) => raised++;
        terminal.Emulator.Reset();
        Assert.Equal(1, raised);
    }

    [Fact]
    public void EmptyFeedRaisesNoEvent()
    {
        var terminal = new TestTerminal();
        int raised = 0;
        terminal.Emulator.Invalidated += (_, _) => raised++;
        terminal.Emulator.Feed(ReadOnlySpan<byte>.Empty);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void ScrollViewportRaisesSingleEvent()
    {
        var terminal = new TestTerminal(10, 2);
        terminal.Feed("1\r\n2\r\n3");
        int raised = 0;
        terminal.Emulator.Invalidated += (_, _) => raised++;
        terminal.Emulator.ScrollViewport(1);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void NoOpScrollViewportRaisesNoEvent()
    {
        var terminal = new TestTerminal(10, 2);
        int raised = 0;
        terminal.Emulator.Invalidated += (_, _) => raised++;
        terminal.Emulator.ScrollViewport(1);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void LockingSyncRootAroundFeedDoesNotDeadlock()
    {
        var terminal = new TestTerminal();
        lock (terminal.Emulator.SyncRoot)
        {
            terminal.Feed("x");
        }

        Assert.Equal("x", terminal.Line(0));
    }

    [Fact]
    public void ResetLeavesPrimaryScreenActive()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b[?1049hALT");
        terminal.Emulator.Reset();
        Assert.False(terminal.Modes.AlternateScreen);
        Assert.True(terminal.Modes.AutoWrap);
        Assert.Equal(string.Empty, terminal.Line(0));
        terminal.Feed("back");
        Assert.Equal("back", terminal.Line(0));
    }

    [Fact]
    public void AlternateScreenContentSurvivesPrimaryActivity()
    {
        var terminal = new TestTerminal(20, 5);
        terminal.Feed("primary");
        terminal.Feed("\u001b[?1049h\u001b[HALT");
        terminal.Feed("\u001b[?1049l");
        terminal.Feed("\r\nsecond");
        Assert.Equal("primary", terminal.Line(0));
        Assert.Equal("second", terminal.Line(1));
    }
}
