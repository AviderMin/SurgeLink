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
