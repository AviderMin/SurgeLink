using System.Text;
using System.Text.RegularExpressions;

namespace SurgeLink.Agent;

/// <summary>
/// Bounds terminal/file text before it reaches an Agent. The output cursor uses the
/// original text length, so trimming always reports how many leading characters
/// were dropped and callers advance the cursor by the same amount.
/// </summary>
internal static partial class AgentText
{
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\)|[PX^_].*?(?:\x1B\\)|[@-_])", RegexOptions.Singleline)]
    private static partial Regex AnsiSequence();

    public static string StripAnsi(string text) => AnsiSequence().Replace(text, string.Empty);

    public static TrimmedText Trim(string text, bool stripAnsi, int? maxBytes, int? maxLines)
    {
        var originalChars = text.Length;
        var start = 0;

        if (maxLines is int lineLimit && lineLimit > 0)
        {
            start = StartOfLastLines(text, lineLimit);
        }

        if (maxBytes is int byteLimit && byteLimit > 0)
        {
            start = Math.Max(start, StartWithinUtf8Bytes(text, start, byteLimit));
        }

        var kept = start == 0 ? text : text[start..];
        if (stripAnsi) kept = StripAnsi(kept);
        return new TrimmedText(kept, start, start > 0, originalChars);
    }

    private static int StartOfLastLines(string text, int lineLimit)
    {
        var end = text.Length;
        var lines = 0;
        for (var index = text.Length - 1; index >= 0; index--)
        {
            if (text[index] != '\n') continue;
            lines++;
            if (lines >= lineLimit) return index + 1;
        }
        return 0;
    }

    private static int StartWithinUtf8Bytes(string text, int start, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(text.AsSpan(start)) <= maximumBytes) return start;

        var bytes = 0;
        var index = start;
        while (index < text.Length)
        {
            var width = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                ? 4
                : text[index] <= 0x7F ? 1 : text[index] <= 0x7FF ? 2 : 3;
            if (bytes + width > maximumBytes) break;
            bytes += width;
            index += width == 4 ? 2 : 1;
        }
        // Never leave a dangling high surrogate at the cut.
        if (index > start && index < text.Length && char.IsLowSurrogate(text[index])) index--;
        return index;
    }
}

internal sealed record TrimmedText(string Text, int DroppedCharacters, bool Truncated, int OriginalCharacters);
