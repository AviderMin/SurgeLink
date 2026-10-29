using System.Globalization;
using System.Text;

namespace SurgeLink.Core.Security;

/// <summary>Escapes untrusted text so one diagnostic cannot forge log lines or terminal controls.</summary>
public static class DiagnosticText
{
    public static string Escape(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                result.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else result.Append(c);
        }
        return result.ToString();
    }
}
