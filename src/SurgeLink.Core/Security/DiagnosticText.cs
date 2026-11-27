using System.Buffers;
using System.Globalization;
using System.Text;

namespace SurgeLink.Core.Security;

/// <summary>Escapes untrusted text so one diagnostic cannot forge log lines or terminal controls.</summary>
public static class DiagnosticText
{
    public static string Escape(string text)
    {
        var result = new StringBuilder(text.Length);
        var span = text.AsSpan();
        for (var index = 0; index < span.Length;)
        {
            var status = Rune.DecodeFromUtf16(span[index..], out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                result.Append("\\u").Append(((int)span[index]).ToString("X4", CultureInfo.InvariantCulture));
                index += Math.Max(1, consumed);
                continue;
            }

            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                if (rune.IsBmp)
                {
                    result.Append("\\u").Append(rune.Value.ToString("X4", CultureInfo.InvariantCulture));
                }
                else
                {
                    result.Append("\\U").Append(rune.Value.ToString("X8", CultureInfo.InvariantCulture));
                }
            }
            else
            {
                result.Append(span.Slice(index, consumed));
            }

            index += consumed;
        }

        return result.ToString();
    }
}
