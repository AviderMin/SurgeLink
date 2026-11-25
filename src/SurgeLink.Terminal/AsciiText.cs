using System.Text;

namespace SurgeLink.Terminal;

/// <summary>
/// Cached single character strings for ASCII. Printing a plain ASCII run then
/// only copies references into cells and never allocates.
/// </summary>
internal static class AsciiText
{
    private static readonly string[] Values = CreateValues();

    /// <summary>Returns the shared string for an ASCII code point.</summary>
    public static string Get(int asciiCode) => Values[asciiCode];

    private static string[] CreateValues()
    {
        var values = new string[0x80];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = ((char)i).ToString();
        }

        return values;
    }
}

/// <summary>A fixed-size cache of immutable single-code-point strings.</summary>
internal static class RuneText
{
    private static readonly string?[] Slots = new string?[8192];

    public static string Get(Rune rune)
    {
        int slot = rune.Value & (Slots.Length - 1);
        string? text = Volatile.Read(ref Slots[slot]);
        if (text is not null && Rune.GetRuneAt(text, 0) == rune) { return text; }
        text = rune.ToString();
        Volatile.Write(ref Slots[slot], text);
        return text;
    }
}
