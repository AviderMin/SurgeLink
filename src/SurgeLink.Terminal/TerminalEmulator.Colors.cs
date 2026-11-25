using System.Globalization;
using System.Text;

namespace SurgeLink.Terminal;

public sealed partial class TerminalEmulator
{
    private TerminalPalette _basePalette = TerminalPalette.Default;
    private readonly Dictionary<byte, uint> _indexedOverrides = new();
    private readonly uint?[] _dynamicColors = new uint?[3];

    /// <summary>The configured theme with the live shell's color overrides applied.</summary>
    public TerminalPalette Palette { get; private set; } = TerminalPalette.Default;

    /// <summary>Sets the theme used for defaults and for subsequent color resets.</summary>
    public void SetBasePalette(TerminalPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        lock (_sync)
        {
            if (ReferenceEquals(_basePalette, palette)) { return; }
            BeginBatch();
            try
            {
                _basePalette = palette;
                RefreshPalette();
            }
            finally { EndBatch(); }
        }
    }

    private void ClearColorOverrides()
    {
        _indexedOverrides.Clear();
        Array.Clear(_dynamicColors);
        Palette = _basePalette;
    }

    private void RefreshPalette()
    {
        Palette = _indexedOverrides.Count == 0 && !_dynamicColors.Any(color => color.HasValue)
            ? _basePalette
            : _basePalette with
            {
                Foreground = _dynamicColors[0] ?? _basePalette.Foreground,
                Background = _dynamicColors[1] ?? _basePalette.Background,
                Cursor = _dynamicColors[2] ?? _basePalette.Cursor,
                Overrides = _indexedOverrides.Count == 0 ? null : new Dictionary<byte, uint>(_indexedOverrides),
            };
        MarkDirty();
    }

    private void HandleOscColor(int command, ReadOnlySpan<byte> payload)
    {
        var parts = Encoding.ASCII.GetString(payload).Split(';');
        var changed = false;
        switch (command)
        {
            case 4:
                for (var i = 0; i + 1 < parts.Length; i += 2)
                {
                    if (!byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                        continue;
                    var spec = parts[i + 1];
                    if (spec == "?")
                    {
                        var rgb = _indexedOverrides.TryGetValue(index, out var value) ? value : _basePalette.ResolveIndexed(index);
                        ReplyColor(FormattableString.Invariant($"4;{index}"), rgb);
                    }
                    else if (TryParseOscColor(spec, out var rgb))
                    {
                        _indexedOverrides[index] = rgb;
                        changed = true;
                    }
                }
                break;
            case 104:
                if (payload.IsEmpty)
                {
                    changed = _indexedOverrides.Count != 0;
                    _indexedOverrides.Clear();
                }
                else
                {
                    foreach (var part in parts)
                        if (byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                            changed |= _indexedOverrides.Remove(index);
                }
                break;
            case 10 or 11 or 12:
                for (var i = 0; i < parts.Length && command + i <= 12; i++)
                {
                    var slot = command + i - 10;
                    if (parts[i] == "?")
                    {
                        var rgb = _dynamicColors[slot] ?? (slot switch
                        {
                            0 => _basePalette.Foreground,
                            1 => _basePalette.Background,
                            _ => _basePalette.Cursor,
                        });
                        ReplyColor((command + i).ToString(CultureInfo.InvariantCulture), rgb);
                    }
                    else if (TryParseOscColor(parts[i], out var rgb))
                    {
                        _dynamicColors[slot] = rgb;
                        changed = true;
                    }
                }
                break;
            case 110 or 111 or 112 when payload.IsEmpty:
                changed = _dynamicColors[command - 110].HasValue;
                _dynamicColors[command - 110] = null;
                break;
        }
        if (changed) { RefreshPalette(); }
    }

    private void ReplyColor(string command, uint rgb)
    {
        var red = ((rgb >> 16) & 255) * 257;
        var green = ((rgb >> 8) & 255) * 257;
        var blue = (rgb & 255) * 257;
        ResponseRequested?.Invoke(FormattableString.Invariant($"\x1b]{command};rgb:{red:x4}/{green:x4}/{blue:x4}\x1b\\"));
    }

    private static bool TryParseOscColor(string spec, out uint rgb)
    {
        rgb = 0;
        if (spec.StartsWith('#'))
        {
            var digits = spec.AsSpan(1);
            var width = digits.Length / 3;
            if (digits.Length is not (3 or 6 or 9 or 12)) { return false; }
            return TryComponents(digits[..width], digits.Slice(width, width), digits[(2 * width)..], isHash: true, out rgb);
        }
        if (spec.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = spec.AsSpan(4);
            var first = parts.IndexOf('/');
            var last = parts.LastIndexOf('/');
            return first >= 0 && last > first
                && TryComponents(parts[..first], parts[(first + 1)..last], parts[(last + 1)..], isHash: false, out rgb);
        }
        if (spec.StartsWith("rgbi:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = spec[5..].Split('/');
            if (parts.Length != 3) { return false; }
            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    || !double.IsFinite(value) || value < 0 || value > 1)
                    return false;
                rgb = (rgb << 8) | (uint)Math.Round(value * 255, MidpointRounding.AwayFromZero);
            }
            return true;
        }
        return TerminalNamedColors.TryResolve(spec, out rgb);
    }

    private static bool TryComponents(ReadOnlySpan<char> red, ReadOnlySpan<char> green, ReadOnlySpan<char> blue,
        bool isHash, out uint rgb)
    {
        rgb = 0;
        if (!TryComponent(red, isHash, out var r) || !TryComponent(green, isHash, out var g)
            || !TryComponent(blue, isHash, out var b)) { return false; }
        rgb = (r << 16) | (g << 8) | b;
        return true;
    }

    private static bool TryComponent(ReadOnlySpan<char> part, bool isHash, out uint value)
    {
        value = 0;
        if (part.Length is < 1 or > 4 || !uint.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var raw))
            return false;
        var bits = part.Length * 4;
        value = isHash ? (bits < 8 ? raw << (8 - bits) : raw >> (bits - 8))
            : (raw * 255 + ((1u << bits) - 1) / 2) / ((1u << bits) - 1);
        return true;
    }
}
