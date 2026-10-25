using System.Globalization;
using System.Text;

namespace SurgeLink.Terminal;

public enum TerminalMouseAction { Press, Release, Move, Wheel }
public enum TerminalMouseButton { Left, Middle, Right, None, WheelUp = 64, WheelDown, WheelLeft, WheelRight }

/// <summary>Encodes xterm button, drag and any-motion reports. Shift reserves local selection.</summary>
public static class TerminalMouseEncoder
{
    public static byte[]? Encode(int column, int row, TerminalMouseButton button, TerminalMouseAction action,
        TerminalModes modes, bool control = false, bool alt = false, bool shift = false)
    {
        ArgumentNullException.ThrowIfNull(modes);
        if (!modes.MouseTracking || shift || column < 0 || row < 0) { return null; }
        var tracking = modes.MouseTrackingMode;
        if (action == TerminalMouseAction.Move && (tracking is TerminalMouseTrackingMode.None or TerminalMouseTrackingMode.Buttons
            || (tracking == TerminalMouseTrackingMode.Drag && button == TerminalMouseButton.None))) { return null; }
        var code = (int)button;
        if (action == TerminalMouseAction.Release && !modes.SgrMouseEncoding) { code = 3; }
        if (action == TerminalMouseAction.Move) { code |= 32; }
        if (control) { code |= 16; }
        if (alt) { code |= 8; }
        if (modes.SgrMouseEncoding)
        {
            return Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"\u001b[<{code};{(long)column + 1};{(long)row + 1}{(action == TerminalMouseAction.Release ? 'm' : 'M')}"));
        }
        // Classic reports have one byte per coordinate. Never wrap large cells.
        if (column >= 223 || row >= 223) { return null; }
        return [(byte)0x1B, (byte)'[', (byte)'M', (byte)(code + 32), (byte)(column + 33), (byte)(row + 33)];
    }
}
