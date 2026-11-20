using System.Text;

namespace SurgeLink.Agent;

/// <summary>
/// Bounds terminal/file text before it reaches an Agent. Incremental output keeps
/// its leading part so the next cursor continues where the page ended; screens and
/// summaries keep their trailing part because the prompt is the useful end.
/// </summary>
internal static class AgentText
{
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
    /// not count as lines so a small tail still contains the prompt. With
    /// <paramref name="stripAnsi"/>, the limits count visible text only and the
    /// returned page never starts inside an escape sequence.
    /// </summary>
    public static TrimmedText TrimTail(string text, bool stripAnsi, int? maxBytes, int? maxLines)
    {
        var originalChars = text.Length;
        var withoutTrailingNewlines = text.TrimEnd('\n');
        if (withoutTrailingNewlines.Length > 0) text = withoutTrailingNewlines;

        if (stripAnsi)
        {
            var spans = FindAnsiSpans(text);
            var visible = StripAnsi(text, spans);
            var start = 0;
            if (maxLines is int lineLimit && lineLimit > 0)
            {
                start = StartOfLastLines(visible, lineLimit);
            }

            if (maxBytes is int byteLimit && byteLimit > 0)
            {
                start = Math.Max(start, TailStartWithinUtf8Bytes(visible, byteLimit));
            }

            if (start == 0)
            {
                return new TrimmedText(visible, text.Length, false, originalChars);
            }

            var ansiStart = RawIndexAtVisible(text, spans, start);
            return new TrimmedText(visible[start..], text.Length - ansiStart, true, originalChars);
        }

        var rawStart = 0;
        if (maxLines is int rawLineLimit && rawLineLimit > 0)
        {
            rawStart = StartOfLastLines(text, rawLineLimit);
        }

        if (maxBytes is int rawByteLimit && rawByteLimit > 0)
        {
            rawStart = Math.Max(rawStart, TailStartWithinUtf8Bytes(text, rawByteLimit));
        }

        return new TrimmedText(text[rawStart..], text.Length - rawStart, rawStart > 0, originalChars);
    }

    /// <summary>
    /// Keeps the leading part of <paramref name="text"/> up to the same limits.
    /// With <paramref name="stripAnsi"/>, the limits count visible text only and an
    /// escape sequence is never split across a page. A page may exceed the byte
    /// budget by at most one code point so a caller always makes progress.
    /// </summary>
    public static TrimmedText TrimHead(string text, bool stripAnsi, int? maxBytes, int? maxLines)
    {
        if (stripAnsi)
        {
            var spans = FindAnsiSpans(text);
            var visible = StripAnsi(text, spans);
            var end = visible.Length;
            if (maxLines is int lineLimit && lineLimit > 0)
            {
                end = Math.Min(end, EndOfFirstLines(visible, lineLimit));
            }

            if (maxBytes is int byteLimit && byteLimit > 0)
            {
                end = Math.Min(end, EndWithinUtf8Bytes(visible, byteLimit));
            }

            if (end == visible.Length)
            {
                // Consume trailing complete escapes, but leave an unfinished one
                // for the next page: the remaining bytes may arrive later.
                if (spans.Count > 0 && !spans[^1].Complete && spans[^1].Start > 0)
                {
                    return new TrimmedText(visible, spans[^1].Start, true, text.Length);
                }

                return new TrimmedText(visible, text.Length, false, text.Length);
            }

            var ansiEnd = end == 0
                ? RawIndexAtVisible(text, spans, 0)
                : RawEndAfterVisible(text, spans, end);
            return new TrimmedText(visible[..end], ansiEnd, ansiEnd < text.Length, text.Length);
        }

        var rawEnd = text.Length;
        if (maxLines is int rawLineLimit && rawLineLimit > 0)
        {
            rawEnd = Math.Min(rawEnd, EndOfFirstLines(text, rawLineLimit));
        }

        if (maxBytes is int rawByteLimit && rawByteLimit > 0)
        {
            rawEnd = Math.Min(rawEnd, EndWithinUtf8Bytes(text, rawByteLimit));
        }

        return new TrimmedText(text[..rawEnd], rawEnd, rawEnd < text.Length, text.Length);
    }

    /// <summary>Removes complete ANSI sequences and an unfinished trailing sequence.</summary>
    public static string StripAnsi(string text) => StripAnsi(text, FindAnsiSpans(text));

    private static string StripAnsi(string text, List<AnsiSpan> spans)
    {
        if (spans.Count == 0) return text;
        var builder = new StringBuilder(text.Length);
        var index = 0;
        foreach (var span in spans)
        {
            if (span.Start > index)
            {
                builder.Append(text, index, span.Start - index);
            }

            index = Math.Max(index, span.End);
        }

        if (index < text.Length)
        {
            builder.Append(text, index, text.Length - index);
        }

        return builder.ToString();
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

    /// <summary>Raw index of the visible UTF-16 unit, skipping leading escape sequences.</summary>
    private static int RawIndexAtVisible(string text, List<AnsiSpan> spans, int visibleUnit)
    {
        var spanIndex = 0;
        var visible = 0;
        for (var index = 0; index < text.Length;)
        {
            if (spanIndex < spans.Count && spans[spanIndex].Start == index)
            {
                index = spans[spanIndex].End;
                spanIndex++;
                continue;
            }

            if (visible >= visibleUnit) return index;
            Utf8WidthAt(text, index, out var chars);
            visible += chars;
            index += chars;
        }
        return text.Length;
    }

    /// <summary>Raw index just after the visible prefix, before any following escape sequence.</summary>
    private static int RawEndAfterVisible(string text, List<AnsiSpan> spans, int visibleUnits)
    {
        var spanIndex = 0;
        var visible = 0;
        for (var index = 0; index < text.Length;)
        {
            if (spanIndex < spans.Count && spans[spanIndex].Start == index)
            {
                index = spans[spanIndex].End;
                spanIndex++;
                continue;
            }

            Utf8WidthAt(text, index, out var chars);
            visible += chars;
            index += chars;
            if (visible >= visibleUnits) return index;
        }
        return text.Length;
    }

    private readonly record struct AnsiSpan(int Start, int End, bool Complete);

    /// <summary>Splits text into CSI, OSC/DCS-like strings and single-character escapes.</summary>
    private static List<AnsiSpan> FindAnsiSpans(string text)
    {
        List<AnsiSpan>? spans = null;
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] != '\u001B')
            {
                index++;
                continue;
            }

            var start = index;
            if (start + 1 >= text.Length)
            {
                (spans ??= []).Add(new AnsiSpan(start, text.Length, false));
                break;
            }

            var kind = text[start + 1];
            int end;
            bool complete;
            switch (kind)
            {
                case '[':
                    var cursor = start + 2;
                    while (cursor < text.Length && text[cursor] is >= '\u0030' and <= '\u003F') cursor++;
                    while (cursor < text.Length && text[cursor] is >= '\u0020' and <= '\u002F') cursor++;
                    if (cursor < text.Length && text[cursor] is >= '\u0040' and <= '\u007E')
                    {
                        end = cursor + 1;
                        complete = true;
                    }
                    else
                    {
                        end = text.Length;
                        complete = false;
                    }
                    break;
                case ']':
                case 'P':
                case 'X':
                case '^':
                case '_':
                    var terminator = start + 2;
                    end = text.Length;
                    complete = false;
                    while (terminator < text.Length)
                    {
                        if (kind == ']' && text[terminator] == '\u0007')
                        {
                            end = terminator + 1;
                            complete = true;
                            break;
                        }

                        if (text[terminator] == '\u001B' && terminator + 1 < text.Length && text[terminator + 1] == '\\')
                        {
                            end = terminator + 2;
                            complete = true;
                            break;
                        }

                        terminator++;
                    }
                    break;
                default:
                    if (kind is < '\u0040' or > '\u005F')
                    {
                        index++;
                        continue;
                    }

                    end = start + 2;
                    complete = true;
                    break;
            }

            (spans ??= []).Add(new AnsiSpan(start, end, complete));
            if (!complete) break;
            index = end;
        }

        return spans ?? [];
    }
}

internal sealed record TrimmedText(string Text, int KeptCharacters, bool Truncated, int OriginalCharacters);
