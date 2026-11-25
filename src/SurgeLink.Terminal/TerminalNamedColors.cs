using System.Globalization;

namespace SurgeLink.Terminal;

/// <summary>Names from the X.Org RGB database used by the xterm color syntax.</summary>
internal static class TerminalNamedColors
{
    private static readonly Lazy<Dictionary<string, uint>> Colors = new(Load);

    internal static bool TryResolve(string name, out uint rgb) => Colors.Value.TryGetValue(name.Trim(), out rgb);

    private static Dictionary<string, uint> Load()
    {
        using var resource = typeof(TerminalNamedColors).Assembly.GetManifestResourceStream("SurgeLink.Terminal.XorgRgb.txt")!;
        using var reader = new StreamReader(resource);
        var colors = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            var columns = line.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (columns.Length < 4 || !byte.TryParse(columns[0], NumberStyles.None, CultureInfo.InvariantCulture, out var r)
                || !byte.TryParse(columns[1], NumberStyles.None, CultureInfo.InvariantCulture, out var g)
                || !byte.TryParse(columns[2], NumberStyles.None, CultureInfo.InvariantCulture, out var b))
                continue;
            colors[string.Join(' ', columns.Skip(3))] = (uint)((r << 16) | (g << 8) | b);
        }
        return colors;
    }
}
