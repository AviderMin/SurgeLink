namespace SurgeLink.Terminal.Tests;

/// <summary>
/// Search is part of the terminal engine so the renderer can highlight matches
/// without rebuilding text or knowing about ANSI state. These tests pin the line
/// coordinate contract and the wide-glyph column mapping.
/// </summary>
public sealed class TerminalSearchTests
{
    [Fact]
    public void FindsTextInTheVisibleViewport()
    {
        var terminal = new TestTerminal(40, 5);
        terminal.Feed("alpha beta gamma");

        TerminalSearchMatch match = Assert.Single(terminal.Emulator.FindText("beta"));

        Assert.Equal(terminal.Emulator.ScrollbackLength, match.Line);
        Assert.Equal(6, match.StartColumn);
        Assert.Equal(4, match.Length);
    }

    [Fact]
    public void FindsTextInScrollbackWithAbsoluteLineNumbers()
    {
        var terminal = new TestTerminal(20, 3, 100);
        terminal.Feed("one\r\ntwo\r\nthree\r\nfour");

        Assert.Equal(1, terminal.Emulator.ScrollbackLength);
        TerminalSearchMatch first = Assert.Single(terminal.Emulator.FindText("one"));
        TerminalSearchMatch last = Assert.Single(terminal.Emulator.FindText("four"));
        Assert.Equal(0, first.Line);
        Assert.Equal(3, last.Line);
    }

    [Fact]
    public void CaseInsensitiveByDefaultAndCaseSensitiveOnRequest()
    {
        var terminal = new TestTerminal(40, 3);
        terminal.Feed("Error error ERROR");

        Assert.Equal(3, terminal.Emulator.FindText("error").Count);
        TerminalSearchMatch sensitive = Assert.Single(terminal.Emulator.FindText("error", matchCase: true));
        Assert.Equal(6, sensitive.StartColumn);
    }

    [Fact]
    public void WideGlyphsMapMatchesToBothColumns()
    {
        var terminal = new TestTerminal(20, 3);
        terminal.Feed("中文abc");

        TerminalSearchMatch wide = Assert.Single(terminal.Emulator.FindText("文"));
        Assert.Equal(2, wide.StartColumn);
        Assert.Equal(2, wide.Length);
        Assert.Equal(3, wide.EndColumn);

        TerminalSearchMatch narrow = Assert.Single(terminal.Emulator.FindText("abc"));
        Assert.Equal(4, narrow.StartColumn);
        Assert.Equal(3, narrow.Length);
    }

    [Fact]
    public void ReportsOverlappingMatchesOnTheSameLine()
    {
        var terminal = new TestTerminal(20, 3);
        terminal.Feed("aaaa");

        IReadOnlyList<TerminalSearchMatch> matches = terminal.Emulator.FindText("aa");

        Assert.Equal(3, matches.Count);
        Assert.Equal(new[] { 0, 1, 2 }, matches.Select(match => match.StartColumn));
    }

    [Fact]
    public void StopsAtTheRequestedMatchLimit()
    {
        var terminal = new TestTerminal(80, 3);
        terminal.Feed(string.Concat(Enumerable.Repeat("x ", 40)));

        IReadOnlyList<TerminalSearchMatch> matches = terminal.Emulator.FindText("x", maxMatches: 5);

        Assert.Equal(5, matches.Count);
    }

    [Fact]
    public void EmptyQueryReturnsNoMatches()
    {
        var terminal = new TestTerminal(20, 3);
        terminal.Feed("anything");

        Assert.Empty(terminal.Emulator.FindText(string.Empty));
    }

    [Fact]
    public void AlternateScreenHidesThePrimaryScrollbackFromSearch()
    {
        var terminal = new TestTerminal(20, 3, 100);
        terminal.Feed("needle one\r\ntwo\r\nthree\r\nfour");
        Assert.Single(terminal.Emulator.FindText("needle"));

        terminal.Feed("\u001b[?1049h");
        terminal.Feed("alternate screen");

        Assert.Empty(terminal.Emulator.FindText("needle"));
        Assert.Single(terminal.Emulator.FindText("alternate"));
    }

    [Fact]
    public void HiddenCellsAreSearchableOnlyAsSpaces()
    {
        var terminal = new TestTerminal(30, 3);
        terminal.Feed("\u001b[8msecret\u001b[0m visible");

        Assert.Empty(terminal.Emulator.FindText("secret"));
        Assert.Single(terminal.Emulator.FindText("visible"));
    }

    [Fact]
    public void CombiningMarksStayInsideTheirCell()
    {
        var terminal = new TestTerminal(20, 3);
        terminal.Feed("e\u0301x");

        TerminalSearchMatch match = Assert.Single(terminal.Emulator.FindText("e\u0301"));

        Assert.Equal(0, match.StartColumn);
        Assert.Equal(1, match.Length);
    }

    [Fact]
    public void EmojiMatchesCoverBothColumns()
    {
        var terminal = new TestTerminal(20, 3);
        terminal.Feed("A\U0001F600B");

        TerminalSearchMatch match = Assert.Single(terminal.Emulator.FindText("\U0001F600"));

        Assert.Equal(1, match.StartColumn);
        Assert.Equal(2, match.Length);
    }

    [Fact]
    public void StylingDoesNotBreakMatchesAcrossColorRuns()
    {
        var terminal = new TestTerminal(40, 3);
        terminal.Feed("\u001b[31mred\u001b[32mgreen\u001b[0m");

        TerminalSearchMatch match = Assert.Single(terminal.Emulator.FindText("redgreen"));

        Assert.Equal(0, match.StartColumn);
        Assert.Equal(8, match.Length);
    }

    [Fact]
    public void SearchReflectsScrollbackEviction()
    {
        var terminal = new TestTerminal(20, 2, 5);
        for (int line = 0; line < 10; line++)
        {
            terminal.Feed("line" + line + "\r\n");
        }

        Assert.Equal(5, terminal.Emulator.ScrollbackLength);
        Assert.Empty(terminal.Emulator.FindText("line0"));
        TerminalSearchMatch match = Assert.Single(terminal.Emulator.FindText("line9"));
        Assert.Equal(terminal.Emulator.ScrollbackLength, match.Line);
    }

    [Fact]
    public void LargeScrollbackSearchStaysWithinABudget()
    {
        var terminal = new TestTerminal(120, 40, 10000);
        for (int line = 0; line < 10000; line++)
        {
            terminal.Feed("line " + line + " abcdefghijklmnopqrstuvwxyz\r\n");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<TerminalSearchMatch> matches = terminal.Emulator.FindText("line 9999");
        stopwatch.Stop();

        Assert.Single(matches);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"search took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void TextAfterWrapOnTheLastRowIsFoundOnItsLine()
    {
        var terminal = new TestTerminal(10, 2, 100);
        terminal.Feed("0123456789AB");

        TerminalSearchMatch match = Assert.Single(terminal.Emulator.FindText("AB"));

        Assert.Equal(terminal.Emulator.ScrollbackLength + 1, match.Line);
        Assert.Equal(0, match.StartColumn);
        Assert.Equal(2, match.Length);
    }
}
