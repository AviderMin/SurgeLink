namespace SurgeLink.Terminal;

/// <summary>Portable names for terminal keys. These never inject Windows desktop input.</summary>
public sealed record TerminalKeyChord(int VirtualKey, bool Control, bool Alt, bool Shift)
{
    public static TerminalKeyChord Parse(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var parts = key.ToUpperInvariant().Split('+');
        var modifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts[..^1])
            if (part is not ("CTRL" or "ALT" or "SHIFT") || !modifiers.Add(part))
                throw new ArgumentException("Use Ctrl, Alt or Shift followed by a terminal key.", nameof(key));
        var name = parts[^1];
        var code = name switch
        {
            "ENTER" or "RETURN" => 0x0D, "TAB" => 0x09, "ESC" or "ESCAPE" => 0x1B,
            "BACKSPACE" => 0x08, "SPACE" => 0x20, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22,
            "END" => 0x23, "HOME" => 0x24, "LEFT" => 0x25, "UP" => 0x26,
            "RIGHT" => 0x27, "DOWN" => 0x28, "INSERT" => 0x2D, "DELETE" => 0x2E,
            "[" => 0xDB, "\\" => 0xDC, "]" => 0xDD,
            _ when name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]) => name[0],
            _ when name.StartsWith('F') && int.TryParse(name.AsSpan(1), out var f) && f is >= 1 and <= 12 => 0x6F + f,
            _ => throw new ArgumentException("Unknown terminal key.", nameof(key)),
        };
        return new(code, modifiers.Contains("CTRL"), modifiers.Contains("ALT"), modifiers.Contains("SHIFT"));
    }
}
