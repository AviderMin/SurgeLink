using System.Text.RegularExpressions;

namespace SurgeLink.App.Services;

/// <summary>Masks known endpoints without altering surrounding names or ports.</summary>
internal static class AddressMasker
{
    public static string Mask(string text, IEnumerable<string> hosts) => CreateMatcher(hosts)?.Replace(text, "***") ?? text;

    public static Regex? CreateMatcher(IEnumerable<string> hosts)
    {
        var pattern = string.Join("|", hosts.Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(h => h.Length)
            .Select(Regex.Escape));
        // Boundaries prevent a short hostname from corrupting unrelated words.
        return pattern.Length == 0 ? null : new Regex(
            @"(?<![\p{L}\p{N}_.-])(?:" + pattern + @")(?![\p{L}\p{N}_.-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
