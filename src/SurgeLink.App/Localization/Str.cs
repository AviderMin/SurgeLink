using SurgeLink.App.Services;

namespace SurgeLink.App.Localization;

/// <summary>
/// Ergonomic access to localized text. Every user-visible string in the UI is
/// written as <c>Str.T("area.key", "English text")</c> so the English copy stays
/// next to the code and the Chinese table only needs the key.
/// </summary>
public static class Str
{
    /// <summary>Returns the text for <paramref name="key"/>, or <paramref name="english"/>.</summary>
    public static string T(string key, string english) =>
        AppServices.Current?.Localization.Get(key, english) ?? english;

    /// <summary>Returns a formatted string for <paramref name="key"/>.</summary>
    public static string F(string key, string english, params object[] args) =>
        AppServices.Current?.Localization.Format(key, english, args)
        ?? string.Format(System.Globalization.CultureInfo.CurrentCulture, english, args);

    /// <summary>True when the UI is currently Chinese; useful for layout tweaks.</summary>
    public static bool IsChinese => AppServices.Current?.Localization.IsChinese ?? false;
}
