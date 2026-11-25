namespace SurgeLink.Core.Files;

/// <summary>
/// Converts between document text and the CRLF buffer used by a multiline WinUI TextBox.
/// Offsets are UTF-16 code-unit offsets, matching TextBox.SelectionStart.
/// </summary>
public static class EditorTextBuffer
{
    public static string ToEditorText(string? documentText)
        => string.IsNullOrEmpty(documentText) ? string.Empty : Normalize(documentText, "\r\n");

    public static string ToDocumentText(string? editorText)
        => editorText is null ? string.Empty : editorText.Contains('\r') ? Normalize(editorText, "\n") : editorText;

    /// <summary>Compares a native buffer with canonical LF text without allocating a copy.</summary>
    public static bool MatchesDocument(string editorText, string documentText)
    {
        if (editorText.Length == documentText.Length && !editorText.Contains('\r'))
            return string.Equals(editorText, documentText, StringComparison.Ordinal);
        int offset = 0;
        for (int i = 0; i < editorText.Length; i++)
        {
            char value = editorText[i];
            if (value == '\r')
            {
                if (i + 1 < editorText.Length && editorText[i + 1] == '\n') { i++; }
                value = '\n';
            }
            if (offset == documentText.Length || documentText[offset++] != value) { return false; }
        }
        return offset == documentText.Length;
    }

    public static int ToDocumentOffset(string editorText, int editorOffset)
    {
        ArgumentNullException.ThrowIfNull(editorText);
        var limit = Math.Clamp(editorOffset, 0, editorText.Length);
        var offset = 0;
        for (var index = 0; index < limit; index++)
        {
            if (editorText[index] == '\r' && index + 1 < editorText.Length && editorText[index + 1] == '\n')
            {
                index++;
            }
            offset++;
        }
        return offset;
    }

    public static int ToEditorOffset(string documentText, int documentOffset)
    {
        ArgumentNullException.ThrowIfNull(documentText);
        var limit = Math.Clamp(documentOffset, 0, documentText.Length);
        var offset = 0;
        for (var index = 0; index < limit; index++)
        {
            if (documentText[index] == '\r')
            {
                if (index + 1 < documentText.Length && documentText[index + 1] == '\n') { index++; }
                offset += 2;
            }
            else
            {
                offset += documentText[index] == '\n' ? 2 : 1;
            }
        }
        return offset;
    }

    private static string Normalize(string text, string newLine)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') { index++; }
                builder.Append(newLine);
            }
            else if (character == '\n')
            {
                builder.Append(newLine);
            }
            else
            {
                builder.Append(character);
            }
        }
        return builder.ToString();
    }
}
