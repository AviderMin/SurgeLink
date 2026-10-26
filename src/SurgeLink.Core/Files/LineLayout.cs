namespace SurgeLink.Core.Files;

/// <summary>
/// Line bookkeeping shared by the text editor and its testable core. A single
/// definition keeps the gutter, the status bar and the caret mapping from
/// disagreeing about what a line break is.
/// </summary>
public static class LineLayout
{
    /// <summary>
    /// Counts the lines the editor displays. A final line break opens an empty
    /// line, so "a\n" is two lines and looks the same to <see cref="LineAt"/>.
    /// </summary>
    public static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return 1; }
        var count = 1;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n') { count++; }
        }
        return count;
    }

    /// <summary>1-based line number of a UTF-16 caret offset, clamped to the text.</summary>
    public static int LineAt(string? text, int offset)
    {
        if (string.IsNullOrEmpty(text) || offset <= 0) { return 1; }
        var limit = Math.Min(offset, text.Length);
        var line = 1;
        for (var index = 0; index < limit; index++)
        {
            if (text[index] == '\n') { line++; }
        }
        return line;
    }

    /// <summary>Gutter labels for a document, aligned with <see cref="CountLines"/>.</summary>
    public static string GutterText(int lineCount)
    {
        if (lineCount <= 0) { return "1"; }
        var builder = new System.Text.StringBuilder(lineCount * 3);
        for (var number = 1; number <= lineCount; number++)
        {
            if (number > 1) { builder.Append('\n'); }
            builder.Append(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>
    /// Normalises text for a <c>TextBox</c>. WinUI's single-line mode collapses
    /// embedded breaks, so a break in the source must never reach the control as
    /// a removable character: replacing the breaks with spaces keeps every
    /// character visible and keeps caret offsets in step with the document.
    /// </summary>
    public static string CollapseForSingleLine(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
}
