namespace SurgeLink.Protocols.Demo;

/// <summary>ANSI escape helpers so the demo output is readable C#.</summary>
internal static class Ansi
{
    /// <summary>The ESC control character.</summary>
    public const char EscapeChar = (char)27;

    /// <summary>ESC as a string.</summary>
    public static readonly string Esc = EscapeChar.ToString();

    /// <summary>Builds an SGR sequence such as ESC[1;32m.</summary>
    public static string Sgr(string codes) => Esc + "[" + codes + "m";

    /// <summary>Select graphic rendition reset.</summary>
    public static readonly string Reset = Sgr("0");

    /// <summary>Clear screen and move the cursor home.</summary>
    public static readonly string ClearScreen = Esc + "[2J" + Esc + "[H";

    /// <summary>Clear the scrollback buffer too.</summary>
    public static readonly string ClearScrollback = Esc + "[2J" + Esc + "[3J" + Esc + "[H";

    /// <summary>Carriage return + line feed.</summary>
    public static readonly string Crlf = "\r\n";

    /// <summary>Wraps text in a colour (SGR code list).</summary>
    public static string Paint(string text, string codes) => Sgr(codes) + text + Reset;
}
