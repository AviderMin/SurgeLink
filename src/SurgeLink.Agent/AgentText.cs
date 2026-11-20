using System.Text;
using System.Text.RegularExpressions;

namespace SurgeLink.Agent;

/// <summary>
/// Bounds terminal/file text before it reaches an Agent. Incremental output keeps
/// its leading part so the next cursor continues where the page ended; screens and
/// summaries keep their trailing part because the prompt is the useful end.
/// </summary>
internal static partial class AgentText
{
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\)|[PX^_].*?(?:\x1B\\)|[@-_])", RegexOptions.Singleline)]
    private static partial Regex AnsiSequence();

    public static string StripAnsi(string text) => AnsiSequence().Replace(text, string.Empty);

    /// <summary>
    /// Removes the leading PTY echo of a just-sent command. Some terminals echo the
    /// paste once locally and once from the remote line discipline, so allow a
    /// small number of adjacent repeats while never touching later output.
    /// </summary>
    public static string StripLeadingEcho(string text, string? echoText)
    {
        if (string.IsNullOrEmpty(echoText) || text.Length == 0) return text;
        var result = text;
        var removed = false;
        for (var repeat = 0; repeat < 3; repeat++)
        {
            var index = result.IndexOf(echoText, StringComparison.Ordinal);
            if (index < 0 || index > 64) break;
            result = result.Remove(index, echoText.Length);
            removed = true;
        }
        return removed ? result.TrimStart('\r', '\n') : result;
    }

    /// <summary>
    /// Keeps the final lines of <paramref name="text"/>. Trailing padding newlines do
    /// not count as lines so a small tail still contains the prompt.
    /// </summary>
    public static TrimmedText TrimTail(string text, bool stripAnsi, int? maxBytes, int? maxLines)
    {
        var originalChars = text.Length;
        var withoutTrailingNewlines = text.TrimEnd('\n');
        if (withoutTrailingNewlines.Length > 0) text = withoutTrailingNewlines;
        var start = 0;

        if (maxLines is int lineLimit && lineLimit > 0)
        {
            start = StartOfLastLines(text, lineLimit);
        }

        if (maxBytes is int byteLimit && byteLimit > 0)
        {
            start = Math.Max(start, TailStartWithinUtf8Bytes(text, byteLimit));
        }

        var kept = start == 0 ? text : text[start..];
        if (stripAnsi) kept = StripAnsi(kept);
        return new TrimmedText(kept, text.Length - start, start > 0, originalChars);
    }

    /// <summary>
    /// Keeps the leading part of <paramref name="text"/> up to the same limits.
    /// <paramref name="maxBytes"/> may be exceeded by at most one code point so a
    /// caller always makes progress.
    /// </summary>
    public static TrimmedText TrimHead(string text, bool stripAnsi, int? maxBytes, int? maxLines)
    {
        var end = text.Length;
        if (maxLines is int lineLimit && lineLimit > 0)
        {
            end = Math.Min(end, EndOfFirstLines(text, lineLimit));
        }

        if (maxBytes is int byteLimit && byteLimit > 0)
        {
            end = Math.Min(end, EndWithinUtf8Bytes(text, byteLimit));
        }

        var kept = text[..end];
        if (stripAnsi) kept = StripAnsi(kept);
        return new TrimmedText(kept, end, end < text.Length, text.Length);
    }

    private static int StartOfLastLines(string text, int lineLimit)
    {
        var lines = 0;
        for (var index = text.Length - 1; index >= 0; index--)
        {
            if (text[index] != '\n') continue;
            lines++;
            if (lines >= lineLimit) return index + 1;
        }
        return 0;
    }

    private static int EndOfFirstLines(string text, int lineLimit)
    {
        var lines = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n') continue;
            lines++;
            if (lines >= lineLimit) return index + 1;
        }
        return text.Length;
    }

    /// <summary>Returns the end index of the longest prefix that fits the byte budget.</summary>
    private static int EndWithinUtf8Bytes(string text, int maximumBytes)
    {
        var bytes = 0;
        var index = 0;
        while (index < text.Length)
        {
            var width = Utf8WidthAt(text, index, out var chars);
            if (bytes + width > maximumBytes) break;
            bytes += width;
            index += chars;
        }
        // An incomplete character must never yield an empty page; include it even
        // when it is a little wider than the requested budget.
        if (index == 0 && text.Length > 0) index += Utf8CharsAt(text, 0);
        return index;
    }

    /// <summary>Returns the start index of the longest suffix that fits the byte budget.</summary>
    private static int TailStartWithinUtf8Bytes(string text, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maximumBytes) return 0;

        var bytes = 0;
        var index = text.Length;
        while (index > 0)
        {
            var width = Utf8WidthBefore(text, index, out var chars);
            if (bytes + width > maximumBytes) break;
            bytes += width;
            index -= chars;
        }
        // Keep at least the final character even when a single code point exceeds
        // the budget, otherwise a tiny limit would return an empty summary.
        if (index == text.Length && index > 0) index -= Utf8CharsBefore(text, index);
        return index;
    }

    private static int Utf8WidthAt(string text, int index, out int chars)
    {
        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            chars = 2;
            return 4;
        }
        chars = 1;
        return text[index] <= 0x7F ? 1 : text[index] <= 0x7FF ? 2 : 3;
    }

    private static int Utf8CharsAt(string text, int index)
    {
        Utf8WidthAt(text, index, out var chars);
        return chars;
    }

    private static int Utf8WidthBefore(string text, int end, out int chars)
    {
        var last = text[end - 1];
        if (char.IsLowSurrogate(last) && end >= 2 && char.IsHighSurrogate(text[end - 2]))
        {
            chars = 2;
            return 4;
        }
        chars = 1;
        return last <= 0x7F ? 1 : last <= 0x7FF ? 2 : 3;
    }

    private static int Utf8CharsBefore(string text, int end)
    {
        Utf8WidthBefore(text, end, out var chars);
        return chars;
    }
}

internal sealed record TrimmedText(string Text, int KeptCharacters, bool Truncated, int OriginalCharacters);
