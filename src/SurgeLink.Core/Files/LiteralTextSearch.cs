namespace SurgeLink.Core.Files;

/// <summary>Literal document search with wraparound and UTF-16 selection offsets.</summary>
public static class LiteralTextSearch
{
    public static int Find(string text, string query, int start, bool previous, bool matchCase)
    {
        if (query.Length == 0 || query.Length > text.Length) { return -1; }
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!previous)
        {
            var index = text.IndexOf(query, Math.Clamp(start, 0, text.Length), comparison);
            return index >= 0 ? index : text.IndexOf(query, comparison);
        }
        // Search by match start so a previous match can end after the caret.
        var lastStart = text.Length - query.Length;
        var limit = Math.Min(start, lastStart);
        if (limit >= 0)
        {
            var index = text.AsSpan(0, limit + query.Length).LastIndexOf(query.AsSpan(), comparison);
            if (index >= 0) return index;
        }
        return text.LastIndexOf(query, comparison);
    }
}
