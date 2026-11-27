using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace SurgeLink.App.Views.Terminal;

/// <summary>Keyboard half of <see cref="TerminalSurface"/>.</summary>
internal sealed partial class TerminalSurface
{
    /// <summary>
    /// Set when the key we just handled may also raise CharacterReceived. WinUI
    /// suppresses it for handled keys, but matching the expected control character or
    /// modifier chord avoids swallowing an unrelated printable keystroke.
    /// </summary>
    private char? _suppressedCharacter;
    private bool _suppressModifierChar;

    /// <summary>First half of a surrogate pair, waiting for its partner.</summary>
    private char _pendingHighSurrogate;

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _suppressedCharacter = null;
        _suppressModifierChar = false;
        if (IsSearchBarEvent(e.OriginalSource)) { return; }

        bool control = IsKeyDown(VirtualKey.Control);
        bool shift = IsKeyDown(VirtualKey.Shift);
        bool alt = IsKeyDown(VirtualKey.Menu);
        int key = (int)e.Key;

        // Ctrl+Shift+F matches Windows Terminal and leaves plain Ctrl+F (0x06) and
        // F3 available to readline, less and other full-screen programs.
        if (control && shift && !alt && key == (int)VirtualKey.F)
        {
            OpenSearch();
            e.Handled = true;
            return;
        }

        if (!control && !alt && e.Key == VirtualKey.F3 && IsSearchVisible)
        {
            NavigateSearch(shift ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (!control && !alt && e.Key == VirtualKey.Escape && IsSearchVisible)
        {
            CloseSearch();
            e.Handled = true;
            return;
        }

        // AltGr is reported as Ctrl+Right Alt on Windows. Let the keyboard layout
        // produce its character instead of encoding it as a terminal shortcut.
        if (control && alt && IsKeyDown(VirtualKey.RightMenu) && IsTextInputKey(e.Key))
        {
            return;
        }

        if ((!control && !alt && e.Key == VirtualKey.Application)
            || (shift && !control && !alt && e.Key == VirtualKey.F10))
        {
            ShowContextMenu(new Windows.Foundation.Point(0, 0));
            e.Handled = true;
            return;
        }

        // --- clipboard: these combos must never reach the shell -------------------
        if (control && shift && !alt && key == (int)VirtualKey.C)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        if (control && shift && !alt && key == (int)VirtualKey.V)
        {
            PasteFromClipboard();
            e.Handled = true;
            return;
        }

        if (control && shift && !alt && key == (int)VirtualKey.A)
        {
            SelectAll();
            e.Handled = true;
            return;
        }

        if (control && !alt && key == (int)VirtualKey.C && HasSelection)
        {
            // With a selection Ctrl+C copies, as in Windows Terminal; without one it
            // falls through and sends ETX so the shell can interrupt a command.
            CopySelection();
            e.Handled = true;
            return;
        }

        if (control && !alt && key == (int)VirtualKey.V)
        {
            PasteFromClipboard();
            e.Handled = true;
            return;
        }

        if (control && !alt && key == (int)VirtualKey.Insert)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        if (shift && !control && !alt && key == (int)VirtualKey.Insert)
        {
            PasteFromClipboard();
            e.Handled = true;
            return;
        }

        // --- font zoom, persisted to settings -------------------------------------
        if (control && !alt)
        {
            if (key is 0xBB or 0x6B)
            {
                ZoomFont(1);
                e.Handled = true;
                return;
            }

            // Ctrl+_/Ctrl+Shift+- is a common readline and terminal shortcut (0x1F),
            // so zoom-out must not consume the shifted OEM-minus form. The numpad
            // key has no terminal control mapping, so it always zooms.
            if (key == 0x6D || (!shift && key == 0xBD))
            {
                ZoomFont(-1);
                e.Handled = true;
                return;
            }

            if (key is 0x30 or 0x60)
            {
                ResetFontSize();
                e.Handled = true;
                return;
            }
        }

        // --- scrollback before the encoder turns these into CSI sequences ---------
        if (shift && !control && !alt)
        {
            if (e.Key == VirtualKey.PageUp)
            {
                ScrollViewportByPage(back: true);
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.PageDown)
            {
                ScrollViewportByPage(back: false);
                e.Handled = true;
                return;
            }
        }

        if (!control && !alt && IsTextInputKey(e.Key))
        {
            // Printable characters arrive through CharacterReceived, which already
            // applies dead keys, IME composition and keyboard layout.
            return;
        }

        if (e.Key is VirtualKey.None or VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows or VirtualKey.Application
            or VirtualKey.CapitalLock or VirtualKey.NumberKeyLock
            || key == 229) // VK_PROCESSKEY: active IME composition owns this keystroke.
        {
            return;
        }

        if (_bridge.SendKey(key, control, alt, shift))
        {
            e.Handled = true;
            _suppressedCharacter = e.Key switch
            {
                VirtualKey.Enter => '\r',
                VirtualKey.Tab => '\t',
                _ => null,
            };
            _suppressModifierChar = control || alt;
        }
        // Keys the encoder cannot map (for example the extra < > | key on a German
        // layout, VK_OEM_102) are deliberately left unhandled so CharacterReceived
        // can deliver the layout-specific character. Previously they were marked
        // handled and silently produced nothing.
    }

    private void OnCharacterReceived(object sender, CharacterReceivedRoutedEventArgs e)
    {
        char character = e.Character;
        e.Handled = true;
        if (IsSearchBarEvent(e.OriginalSource)) { return; }

        bool shouldSuppress = (_suppressedCharacter is { } suppressed && character == suppressed)
            || (_suppressModifierChar && (IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu)));
        _suppressedCharacter = null;
        _suppressModifierChar = false;
        if (shouldSuppress)
        {
            return;
        }

        if (character == '\0')
        {
            return;
        }

        if (char.IsControl(character) && character is not ('\t' or '\r'))
        {
            return;
        }

        if (char.IsHighSurrogate(character))
        {
            _pendingHighSurrogate = character;
            return;
        }

        string text;
        if (char.IsLowSurrogate(character) && _pendingHighSurrogate != '\0')
        {
            text = new string(new[] { _pendingHighSurrogate, character });
            _pendingHighSurrogate = '\0';
        }
        else
        {
            _pendingHighSurrogate = '\0';
            if (char.IsLowSurrogate(character)) return;
            text = character.ToString();
        }

        _bridge.SendText(text);
    }

    /// <summary>True for keys whose characters arrive through CharacterReceived.</summary>
    private static bool IsTextInputKey(VirtualKey key)
    {
        int value = (int)key;
        return (value >= 0x30 && value <= 0x39)      // 0-9
               || (value >= 0x41 && value <= 0x5A)   // A-Z
               || value == 0x20                      // space
               || (value >= 0x60 && value <= 0x6F)   // numpad digits and operators
               || (value >= 0xBA && value <= 0xDF);  // OEM punctuation
    }

    private static bool IsKeyDown(VirtualKey key)
    {
        try
        {
            CoreVirtualKeyStates state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
            return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        }
        catch (Exception)
        {
            // Some keyboard sources (touch, remote) cannot report held keys.
            return false;
        }
    }
}
