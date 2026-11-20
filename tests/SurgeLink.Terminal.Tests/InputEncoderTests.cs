using System.Text;

namespace SurgeLink.Terminal.Tests;

public sealed class InputEncoderTests
{
    private const int VkBack = 0x08;
    private const int VkTab = 0x09;
    private const int VkReturn = 0x0D;
    private const int VkEscape = 0x1B;
    private const int VkSpace = 0x20;
    private const int VkPageUp = 0x21;
    private const int VkPageDown = 0x22;
    private const int VkEnd = 0x23;
    private const int VkHome = 0x24;
    private const int VkLeft = 0x25;
    private const int VkUp = 0x26;
    private const int VkRight = 0x27;
    private const int VkDown = 0x28;
    private const int VkInsert = 0x2D;
    private const int VkDelete = 0x2E;
    private const int VkF1 = 0x70;
    private const int VkF2 = 0x71;
    private const int VkF3 = 0x72;
    private const int VkF4 = 0x73;
    private const int VkF5 = 0x74;
    private const int VkF7 = 0x76;
    private const int VkF12 = 0x7B;

    private static readonly TerminalModes Normal = TerminalModes.Default;

    private static readonly TerminalModes Application =
        TerminalModes.Default with { ApplicationCursorKeys = true };

    private static string Encode(int virtualKey, bool ctrl = false, bool alt = false, bool shift = false, TerminalModes? modes = null)
    {
        byte[]? bytes = TerminalInputEncoder.EncodeKey(virtualKey, ctrl, alt, shift, modes ?? Normal);
        Assert.NotNull(bytes);
        return Encoding.UTF8.GetString(bytes!);
    }

    [Fact]
    public void ArrowKeysUseCsiInNormalMode()
    {
        Assert.Equal("\u001b[A", Encode(VkUp));
        Assert.Equal("\u001b[B", Encode(VkDown));
        Assert.Equal("\u001b[C", Encode(VkRight));
        Assert.Equal("\u001b[D", Encode(VkLeft));
    }

    [Fact]
    public void ArrowKeysUseSs3InApplicationMode()
    {
        Assert.Equal("\u001bOA", Encode(VkUp, modes: Application));
        Assert.Equal("\u001bOB", Encode(VkDown, modes: Application));
        Assert.Equal("\u001bOC", Encode(VkRight, modes: Application));
        Assert.Equal("\u001bOD", Encode(VkLeft, modes: Application));
    }

    [Fact]
    public void ModifiedArrowKeysUseXtermModifierSuffix()
    {
        Assert.Equal("\u001b[1;5A", Encode(VkUp, ctrl: true));
        Assert.Equal("\u001b[1;2C", Encode(VkRight, shift: true));
        Assert.Equal("\u001b[1;3D", Encode(VkLeft, alt: true));
    }

    [Fact]
    public void HomeAndEndUseCsiAndSs3Forms()
    {
        Assert.Equal("\u001b[H", Encode(VkHome));
        Assert.Equal("\u001b[F", Encode(VkEnd));
        Assert.Equal("\u001bOH", Encode(VkHome, modes: Application));
        Assert.Equal("\u001bOF", Encode(VkEnd, modes: Application));
    }

    [Fact]
    public void EditingAndPagingKeysUseTildeSequences()
    {
        Assert.Equal("\u001b[2~", Encode(VkInsert));
        Assert.Equal("\u001b[3~", Encode(VkDelete));
        Assert.Equal("\u001b[5~", Encode(VkPageUp));
        Assert.Equal("\u001b[6~", Encode(VkPageDown));
    }

    [Fact]
    public void ModifiedPagingKeysUseModifierSuffix()
    {
        Assert.Equal("\u001b[5;5~", Encode(VkPageUp, ctrl: true));
        Assert.Equal("\u001b[6;2~", Encode(VkPageDown, shift: true));
    }

    [Fact]
    public void FunctionKeysOneToFourUseSs3()
    {
        Assert.Equal("\u001bOP", Encode(VkF1));
        Assert.Equal("\u001bOQ", Encode(VkF2));
        Assert.Equal("\u001bOR", Encode(VkF3));
        Assert.Equal("\u001bOS", Encode(VkF4));
    }

    [Fact]
    public void HigherFunctionKeysUseTildeCodes()
    {
        Assert.Equal("\u001b[15~", Encode(VkF5));
        Assert.Equal("\u001b[18~", Encode(VkF7));
        Assert.Equal("\u001b[24~", Encode(VkF12));
    }

    [Fact]
    public void ModifiedFunctionKeyUsesModifierSuffix()
    {
        Assert.Equal("\u001b[1;2P", Encode(VkF1, shift: true));
        Assert.Equal("\u001b[15;5~", Encode(VkF5, ctrl: true));
    }

    [Fact]
    public void TabAndShiftTab()
    {
        Assert.Equal("\t", Encode(VkTab));
        Assert.Equal("\u001b[Z", Encode(VkTab, shift: true));
    }

    [Fact]
    public void EnterAndAltEnter()
    {
        Assert.Equal("\r", Encode(VkReturn));
        Assert.Equal("\u001b\r", Encode(VkReturn, alt: true));
    }

    [Fact]
    public void BackspaceSendsDelAndCtrlBackspaceSendsBackspace()
    {
        Assert.Equal(new byte[] { 0x7F }, TerminalInputEncoder.EncodeKey(VkBack, false, false, false, Normal)!);
        Assert.Equal(new byte[] { 0x08 }, TerminalInputEncoder.EncodeKey(VkBack, true, false, false, Normal)!);
    }

    [Fact]
    public void EscapeKeySendsEscape()
    {
        Assert.Equal("\u001b", Encode(VkEscape));
        Assert.Equal("\u001b\u001b", Encode(VkEscape, alt: true));
    }

    [Theory]
    [InlineData(0x41, 0x01)]
    [InlineData(0x43, 0x03)]
    [InlineData(0x44, 0x04)]
    [InlineData(0x5A, 0x1A)]
    public void ControlLettersProduceControlCharacters(int virtualKey, int expected)
    {
        byte[]? bytes = TerminalInputEncoder.EncodeKey(virtualKey, ctrl: true, alt: false, shift: false, Normal);
        Assert.Equal(new byte[] { (byte)expected }, bytes!);
    }

    [Fact]
    public void AltLetterIsPrefixedWithEscape()
    {
        Assert.Equal("\u001ba", Encode(0x41, alt: true));
        Assert.Equal("\u001bA", Encode(0x41, alt: true, shift: true));
    }

    [Fact]
    public void ControlAltLetterCombinesBoth()
    {
        Assert.Equal("\u001b\u0003", Encode(0x43, ctrl: true, alt: true));
    }

    [Fact]
    public void ShiftControlsLetterCase()
    {
        Assert.Equal("a", Encode(0x41));
        Assert.Equal("A", Encode(0x41, shift: true));
    }

    [Fact]
    public void ShiftedDigitProducesUsSymbol()
    {
        Assert.Equal("1", Encode(0x31));
        Assert.Equal("!", Encode(0x31, shift: true));
        Assert.Equal(")", Encode(0x30, shift: true));
    }

    [Fact]
    public void SpaceAndControlSpace()
    {
        Assert.Equal(" ", Encode(VkSpace));
        Assert.Equal(new byte[] { 0x00 }, TerminalInputEncoder.EncodeKey(VkSpace, true, false, false, Normal)!);
    }

    [Theory]
    [InlineData(0x32, 0x00)] // Ctrl+2 / Ctrl+@
    [InlineData(0x33, 0x1B)] // Ctrl+3 / Ctrl+[
    [InlineData(0x34, 0x1C)] // Ctrl+4 / Ctrl+\
    [InlineData(0x35, 0x1D)] // Ctrl+5 / Ctrl+]
    [InlineData(0x36, 0x1E)] // Ctrl+6 / Ctrl+^
    [InlineData(0x37, 0x1F)] // Ctrl+7 / Ctrl+_
    [InlineData(0x38, 0x7F)] // Ctrl+8
    public void ControlDigitsProduceAsciiControlCharacters(int virtualKey, int expected)
    {
        byte[]? bytes = TerminalInputEncoder.EncodeKey(virtualKey, ctrl: true, alt: false, shift: false, Normal);
        Assert.Equal(new byte[] { (byte)expected }, bytes!);
    }

    [Fact]
    public void ControlMinusProducesUnitSeparatorInBothShiftStates()
    {
        byte[] unshifted = TerminalInputEncoder.EncodeKey(0xBD, true, false, false, Normal)!;
        byte[] shifted = TerminalInputEncoder.EncodeKey(0xBD, true, false, true, Normal)!;
        Assert.Equal(new byte[] { 0x1F }, unshifted);
        Assert.Equal(new byte[] { 0x1F }, shifted);
    }

    [Fact]
    public void ControlDigitWithAltKeepsTheEscapePrefix()
    {
        Assert.Equal(new byte[] { 0x1B, 0x1E }, TerminalInputEncoder.EncodeKey(0x36, true, true, false, Normal)!);
    }

    [Fact]
    public void ControlQuestionMarkProducesDel()
    {
        Assert.Equal(new byte[] { 0x7F }, TerminalInputEncoder.EncodeKey(0xBF, true, false, true, Normal)!);
        Assert.Equal("?", Encode(0xBF, shift: true));
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0x14)]
    [InlineData(0x5B)]
    [InlineData(0x5C)]
    [InlineData(0x5D)]
    [InlineData(0)]
    public void ModifierKeysProduceNoInput(int virtualKey)
    {
        Assert.Null(TerminalInputEncoder.EncodeKey(virtualKey, false, false, false, Normal));
    }

    [Fact]
    public void UnknownKeyProducesNoInput()
    {
        Assert.Null(TerminalInputEncoder.EncodeKey(0xFFFF, false, false, false, Normal));
    }

    [Fact]
    public void OemBracketKeysMapToCharacters()
    {
        Assert.Equal("[", Encode(0xDB));
        Assert.Equal("{", Encode(0xDB, shift: true));
        Assert.Equal("\u001b", Encode(0xDB, ctrl: true));
        Assert.Equal("]", Encode(0xDD));
        Assert.Equal("}", Encode(0xDD, shift: true));
        Assert.Equal("\\", Encode(0xDC));
        Assert.Equal("|", Encode(0xDC, shift: true));
    }

    [Fact]
    public void OemPunctuationKeysMapToCharacters()
    {
        Assert.Equal(";", Encode(0xBA));
        Assert.Equal(":", Encode(0xBA, shift: true));
        Assert.Equal("=", Encode(0xBB));
        Assert.Equal("+", Encode(0xBB, shift: true));
        Assert.Equal("-", Encode(0xBD));
        Assert.Equal("_", Encode(0xBD, shift: true));
        Assert.Equal("/", Encode(0xBF));
        Assert.Equal("?", Encode(0xBF, shift: true));
    }

    [Fact]
    public void TextIsEncodedAsUtf8()
    {
        Assert.Equal("héllo €", Encoding.UTF8.GetString(TerminalInputEncoder.EncodeText("héllo €", Normal)));
    }

    [Fact]
    public void PasteWithoutBracketedPasteIsRawPayload()
    {
        byte[] encoded = TerminalInputEncoder.EncodePaste("line1\nline2", Normal);
        Assert.Equal("line1\nline2", Encoding.UTF8.GetString(encoded));
    }

    [Fact]
    public void PasteWithBracketedPasteIsWrapped()
    {
        TerminalModes modes = TerminalModes.Default with { BracketedPaste = true };
        byte[] encoded = TerminalInputEncoder.EncodePaste("abc", modes);
        Assert.Equal("\u001b[200~abc\u001b[201~", Encoding.UTF8.GetString(encoded));
    }

    [Fact]
    public void BracketedPasteCannotBeTerminatedByPayload()
    {
        TerminalModes modes = TerminalModes.Default with { BracketedPaste = true };
        byte[] encoded = TerminalInputEncoder.EncodePaste("safe\u001b[201~rm -rf ~\r", modes);
        // The payload's ESC is removed so the only terminator is the one added
        // after the payload; the application cannot mistake it for typed input.
        Assert.Equal("\u001b[200~safe[201~rm -rf ~\r\u001b[201~", Encoding.UTF8.GetString(encoded));
    }

    [Fact]
    public void NonBracketedPasteKeepsControlCharacters()
    {
        // Without bracketed paste there is no frame to escape, so the payload is
        // forwarded unchanged (a user may deliberately paste an escape sequence).
        byte[] encoded = TerminalInputEncoder.EncodePaste("a\u001b[31mb", Normal);
        Assert.Equal("a\u001b[31mb", Encoding.UTF8.GetString(encoded));
    }

    [Fact]
    public void NullArgumentsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => TerminalInputEncoder.EncodeText(null!, Normal));
        Assert.Throws<ArgumentNullException>(() => TerminalInputEncoder.EncodePaste(null!, Normal));
        Assert.Throws<ArgumentNullException>(
            () => TerminalInputEncoder.EncodeKey(0x41, false, false, false, null!));
    }
}
