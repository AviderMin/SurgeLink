using System.Globalization;
using System.Text;

namespace SurgeLink.Terminal;

/// <summary>
/// Translates editor input into the byte sequences a POSIX shell expects.
/// Virtual key codes are the Windows <c>VirtualKey</c> values so the UI layer
/// can pass them straight through without an extra mapping table.
/// </summary>
public static class TerminalInputEncoder
{
    private const int VkBack = 0x08;
    private const int VkTab = 0x09;
    private const int VkReturn = 0x0D;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkCapital = 0x14;
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
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkApps = 0x5D;
    private const int VkF1 = 0x70;
    private const int VkF12 = 0x7B;

    // CSI <code> ~ values for F5 through F12.
    private static readonly int[] FunctionKeyTildeCodes = { 15, 17, 18, 19, 20, 21, 23, 24 };

    /// <summary>Encodes typed text (paste or IME commit) as UTF-8.</summary>
    /// <param name="text">Text to encode.</param>
    /// <param name="modes">Current terminal modes; reserved for future line ending handling.</param>
    public static byte[] EncodeText(string text, TerminalModes modes)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(modes);
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>Encodes a paste, honouring bracketed paste mode.</summary>
    /// <param name="text">Pasted text.</param>
    /// <param name="modes">Current terminal modes.</param>
    public static byte[] EncodePaste(string text, TerminalModes modes)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(modes);
        var payload = Encoding.UTF8.GetBytes(text);
        if (!modes.BracketedPaste)
        {
            return payload;
        }

        const string prefix = "\u001b[200~";
        const string suffix = "\u001b[201~";
        byte[] prefixBytes = Encoding.UTF8.GetBytes(prefix);
        byte[] suffixBytes = Encoding.UTF8.GetBytes(suffix);
        var result = new byte[prefixBytes.Length + payload.Length + suffixBytes.Length];
        prefixBytes.CopyTo(result, 0);
        payload.CopyTo(result, prefixBytes.Length);
        suffixBytes.CopyTo(result, prefixBytes.Length + payload.Length);
        return result;
    }

    /// <summary>
    /// Encodes a special key. Returns <c>null</c> when the key produces no
    /// terminal input (modifier keys, Windows keys, unrecognised keys).
    /// </summary>
    /// <param name="virtualKey">Windows virtual key code.</param>
    /// <param name="ctrl">Control is held.</param>
    /// <param name="alt">Alt is held; the sequence is prefixed with ESC.</param>
    /// <param name="shift">Shift is held.</param>
    /// <param name="modes">Current terminal modes; application cursor keys change arrow, home and end sequences.</param>
    public static byte[]? EncodeKey(int virtualKey, bool ctrl, bool alt, bool shift, TerminalModes modes)
    {
        ArgumentNullException.ThrowIfNull(modes);

        switch (virtualKey)
        {
            case VkShift:
            case VkControl:
            case VkMenu:
            case VkCapital:
            case VkLWin:
            case VkRWin:
            case VkApps:
            case 0:
                return null;
        }

        int modifiers = ModifierParameter(ctrl, alt, shift);

        switch (virtualKey)
        {
            case VkUp:
                return EncodeCursorKey('A', modifiers, modes.ApplicationCursorKeys);
            case VkDown:
                return EncodeCursorKey('B', modifiers, modes.ApplicationCursorKeys);
            case VkRight:
                return EncodeCursorKey('C', modifiers, modes.ApplicationCursorKeys);
            case VkLeft:
                return EncodeCursorKey('D', modifiers, modes.ApplicationCursorKeys);
            case VkHome:
                return EncodeCursorKey('H', modifiers, modes.ApplicationCursorKeys);
            case VkEnd:
                return EncodeCursorKey('F', modifiers, modes.ApplicationCursorKeys);
            case VkInsert:
                return EncodeTildeKey(2, modifiers);
            case VkDelete:
                return EncodeTildeKey(3, modifiers);
            case VkPageUp:
                return EncodeTildeKey(5, modifiers);
            case VkPageDown:
                return EncodeTildeKey(6, modifiers);
            case VkTab:
                return WithAlt(shift ? "\u001b[Z" : "\t", alt);
            case VkReturn:
                return WithAlt("\r", alt);
            case VkEscape:
                return WithAlt("\u001b", alt);
            case VkBack:
                return WithAlt(ctrl ? "\b" : "\u007f", alt);
            case VkSpace:
                return ctrl ? WithAlt("\0", alt) : WithAlt(" ", alt);
            case >= VkF1 and <= VkF12:
                return EncodeFunctionKey(virtualKey - VkF1, modifiers);
            case 0x30:
            case 0x31:
            case 0x32:
            case 0x33:
            case 0x34:
            case 0x35:
            case 0x36:
            case 0x37:
            case 0x38:
            case 0x39:
                return EncodeCharacter(shift ? ShiftedDigits[virtualKey - 0x30] : (char)virtualKey, alt);
            case >= 0x41 and <= 0x5A:
                if (ctrl)
                {
                    return WithAlt(((char)(virtualKey - 0x40)).ToString(), alt);
                }

                return EncodeCharacter(shift ? (char)virtualKey : char.ToLowerInvariant((char)virtualKey), alt);
            case >= 0x60 and <= 0x69:
                return EncodeCharacter((char)('0' + (virtualKey - 0x60)), alt);
            case 0xBA:
                return EncodeCharacter(shift ? ':' : ';', alt);
            case 0xBB:
                return EncodeCharacter(shift ? '+' : '=', alt);
            case 0xBC:
                return EncodeCharacter(shift ? '<' : ',', alt);
            case 0xBD:
                return EncodeCharacter(shift ? '_' : '-', alt);
            case 0xBE:
                return EncodeCharacter(shift ? '>' : '.', alt);
            case 0xBF:
                return EncodeCharacter(shift ? '?' : '/', alt);
            case 0xC0:
                return EncodeCharacter(shift ? '~' : '\u0060', alt);
            case 0xDB:
                return WithAlt(ctrl ? "\u001b" : shift ? "{" : "[", alt);
            case 0xDC:
                return WithAlt(ctrl ? "\u001c" : shift ? "|" : "\\", alt);
            case 0xDD:
                return WithAlt(ctrl ? "\u001d" : shift ? "}" : "]", alt);
            case 0xDE:
                return EncodeCharacter(shift ? '"' : '\u0027', alt);
            default:
                return null;
        }
    }

    private static readonly char[] ShiftedDigits = { ')', '!', '@', '#', '$', '%', '^', '&', '*', '(' };

    private static int ModifierParameter(bool ctrl, bool alt, bool shift) =>
        1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

    private static byte[] EncodeCursorKey(char final, int modifiers, bool applicationCursorKeys)
    {
        // When modifiers are held xterm encodes them in the sequence itself
        // (for example CSI 1;5A for Ctrl+Up), so no separate ESC prefix is added.
        string sequence = modifiers == 1
            ? applicationCursorKeys
                ? "\u001bO" + final
                : "\u001b[" + final
            : "\u001b[1;" + modifiers.ToString(CultureInfo.InvariantCulture) + final;
        return Encoding.UTF8.GetBytes(sequence);
    }

    private static byte[] EncodeTildeKey(int code, int modifiers)
    {
        string number = code.ToString(CultureInfo.InvariantCulture);
        string sequence = modifiers == 1
            ? "\u001b[" + number + "~"
            : "\u001b[" + number + ";" + modifiers.ToString(CultureInfo.InvariantCulture) + "~";
        return Encoding.UTF8.GetBytes(sequence);
    }

    private static byte[] EncodeFunctionKey(int index, int modifiers)
    {
        if (index < 4)
        {
            char final = (char)('P' + index);
            string sequence = modifiers == 1
                ? "\u001bO" + final
                : "\u001b[1;" + modifiers.ToString(CultureInfo.InvariantCulture) + final;
            return Encoding.UTF8.GetBytes(sequence);
        }

        return EncodeTildeKey(FunctionKeyTildeCodes[index - 4], modifiers);
    }

    private static byte[] EncodeCharacter(char character, bool alt) =>
        WithAlt(character.ToString(), alt);

    private static byte[] WithAlt(string sequence, bool alt)
    {
        string text = alt ? "\u001b" + sequence : sequence;
        return Encoding.UTF8.GetBytes(text);
    }
}
