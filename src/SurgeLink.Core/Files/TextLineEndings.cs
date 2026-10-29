namespace SurgeLink.Core.Files;

/// <summary>
/// Line-ending bookkeeping shared by the text editor. The editor keeps its
/// in-memory document canonicalised to LF (<see cref="EditorTextBuffer"/>), but a
/// remote file may use CRLF or legacy CR breaks. Detecting the original
/// convention on load and re-applying it on save keeps edits from silently
/// rewriting every line ending of the whole file.
/// </summary>
public static class TextLineEndings
{
    public const string Lf = "\n";
    public const string CrLf = "\r\n";
    public const string Cr = "\r";

    /// <summary>
    /// Returns the dominant line ending of <paramref name="text"/>: CRLF when it
    /// ties or wins, otherwise CR, otherwise LF (also for text with no break).
    /// </summary>
    public static string Detect(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return Lf; }
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') { crlf++; index++; }
                else { cr++; }
            }
            else if (character == '\n') { lf++; }
        }
        if (crlf > 0 && crlf >= lf && crlf >= cr) { return CrLf; }
        if (cr > 0 && cr > lf) { return Cr; }
        return Lf;
    }

    /// <summary>
    /// Normalises <paramref name="text"/> to LF and then expands each break to
    /// <paramref name="newLine"/>. Idempotent for the target convention.
    /// </summary>
    public static string Apply(string? text, string newLine)
    {
        ArgumentNullException.ThrowIfNull(newLine);
        var canonical = ToLf(text);
        if (newLine == Lf || canonical.Length == 0) { return canonical; }
        if (newLine == CrLf) { return canonical.Replace("\n", "\r\n", StringComparison.Ordinal); }
        if (newLine == Cr) { return canonical.Replace("\n", "\r", StringComparison.Ordinal); }
        return canonical;
    }

    /// <summary>Collapses CRLF and lone CR breaks to LF.</summary>
    public static string ToLf(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
