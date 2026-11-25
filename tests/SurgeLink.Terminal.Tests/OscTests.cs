namespace SurgeLink.Terminal.Tests;

public sealed class OscTests
{
    [Fact]
    public void IndexedPaletteCanSetQueryAndResetMultipleColors()
    {
        var terminal = new TestTerminal();
        var emulator = terminal.Emulator;
        var replies = new List<string>();
        emulator.ResponseRequested += replies.Add;
        terminal.Feed("\u001b[38;5;200mX\u001b]4;1;LightSkyBlue3;200;#123456;200;?;1;?\u0007");

        Assert.Equal(0x123456u, emulator.Palette.Resolve(terminal.Cell(0, 0).Foreground, false, CellAttributes.None));
        Assert.Equal(0x8DB6CDu, emulator.Palette.ResolveIndexed(1));
        Assert.Equal(new[] { "\u001b]4;200;rgb:1212/3434/5656\u0007", "\u001b]4;1;rgb:8d8d/b6b6/cdcd\u0007" }, replies);

        terminal.Feed("\u001b]104;200\u0007");
        Assert.Equal(TerminalPalette.Default.ResolveIndexed(200), emulator.Palette.ResolveIndexed(200));
        Assert.Equal(0x8DB6CDu, emulator.Palette.ResolveIndexed(1));
        terminal.Feed("\u001b]104\u001b\\");
        Assert.Same(TerminalPalette.Default, emulator.Palette);
    }

    [Fact]
    public void DynamicColorsQueryTheEffectiveThemeAndResetToItsLatestDefaults()
    {
        var terminal = new TestTerminal();
        var emulator = terminal.Emulator;
        var replies = new List<string>();
        emulator.ResponseRequested += replies.Add;
        emulator.SetBasePalette(TerminalThemes.SolarizedDark);
        terminal.Feed("\u001b]10;?;rgb:f/0/8;rgbi:0/0.5/1\u001b\\");
        terminal.Feed("\u001b]11;?;?\u0007");
        Assert.Equal(new[]
        {
            "\u001b]10;rgb:8383/9494/9696\u001b\\",
            "\u001b]11;rgb:ffff/0000/8888\u0007",
            "\u001b]12;rgb:0000/8080/ffff\u0007",
        }, replies);

        emulator.SetBasePalette(TerminalThemes.Light);
        Assert.Equal(0xFF0088u, emulator.Palette.Background);
        terminal.Feed("\u001b]111\u001b\\\u001b]112\u0007\u001b]110\u0007");
        Assert.Same(TerminalThemes.Light, emulator.Palette);
    }

    [Fact]
    public void IncompleteColorSequenceAndInvalidValuesDoNotChangePalette()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b]4;256;#123456;0;rgb:zz/0/f\u0007");
        terminal.Feed("\u001b]11;#112233\u001b[31mX");
        Assert.Same(TerminalPalette.Default, terminal.Emulator.Palette);
        Assert.Equal(TerminalColor.FromIndex(1), terminal.Cell(0, 0).Foreground);
    }

    [Fact]
    public void ReopenShellClearsColorOverrides()
    {
        var terminal = new TestTerminal();
        terminal.Emulator.SetBasePalette(TerminalThemes.Light);
        terminal.Feed("\u001b]4;0;#112233\u0007\u001b]10;#112233\u0007");
        terminal.Emulator.ResetForNewShell();
        Assert.Same(TerminalThemes.Light, terminal.Emulator.Palette);
    }

    [Fact]
    public void RisClearsEveryColorOverride()
    {
        var terminal = new TestTerminal();
        terminal.Emulator.SetBasePalette(TerminalThemes.Light);
        terminal.Feed("\u001b]4;1;#112233\u0007\u001b]10;#123456;#abcdef;#fedcba\u0007");
        Assert.NotSame(TerminalThemes.Light, terminal.Emulator.Palette);

        terminal.Feed("\u001bc");

        Assert.Same(TerminalThemes.Light, terminal.Emulator.Palette);
    }

    [Fact]
    public void PaletteQueriesAreBoundedAndDeliveredOutsideTheEmulatorLock()
    {
        var terminal = new TestTerminal();
        var responses = new List<string>();
        terminal.Emulator.ResponseRequested += response =>
        {
            Assert.False(Monitor.IsEntered(terminal.Emulator.SyncRoot));
            responses.Add(response);
        };
        terminal.Feed("\u001b]4;" + string.Concat(Enumerable.Repeat("1;?;", 100)) + "1; #112233 \u001b\\");

        Assert.Equal(32, responses.Count);
        Assert.All(responses, response => Assert.EndsWith("\u001b\\", response));
        Assert.Equal(0x112233u, terminal.Emulator.Palette.ResolveIndexed(1));
    }

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
    public void OscAbortedByNonTerminatorEscapeDoesNotSwallowFollowingCsi()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b]0;title\u001b[31mR\u001b[0m");

        Assert.Equal("title", terminal.Modes.WindowTitle);
        Assert.Equal("R", terminal.Line(0));
    }

    [Fact]
    public void OscAbortedByEscapeIsDispatchedAcrossSplitFeeds()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b]0;title\u001b");
        terminal.Feed("[32mG\u001b[0m");

        Assert.Equal("title", terminal.Modes.WindowTitle);
        Assert.Equal("G", terminal.Line(0));
        Assert.Equal(TerminalColor.FromIndex(2), terminal.Cell(0, 0).Foreground);
    }

    [Fact]
    public void OscAbortedByDoubleEscapeStillStartsCsi()
    {
        var terminal = new TestTerminal();
        terminal.Feed("\u001b]0;title\u001b\u001b[31mR\u001b[0m");

        Assert.Equal("title", terminal.Modes.WindowTitle);
        Assert.Equal("R", terminal.Line(0));
        Assert.Equal(TerminalColor.FromIndex(1), terminal.Cell(0, 0).Foreground);
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
