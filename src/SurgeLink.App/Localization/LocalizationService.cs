using System.Globalization;
using SurgeLink.App.Services;
using SurgeLink.App.Localization.Zh;

namespace SurgeLink.App.Localization;

/// <summary>
/// Resolves UI text for the current language. English lives at the call site
/// (<c>Str.T("key", "English")</c>); translations are registered as
/// key -> text dictionaries so a missing translation degrades to English
/// instead of showing a raw key.
/// </summary>
public sealed class LocalizationService
{
    private readonly IAppSettingsStore _settings;
    private readonly Dictionary<string, string> _chinese = new(StringComparer.Ordinal);
    private AppLanguage _configured;
    private bool _isChinese;

    /// <summary>Creates the service and registers the shipped translation tables.</summary>
    public LocalizationService(IAppSettingsStore settings)
    {
        _settings = settings;
        Register(ZhCommon.Map);
        Register(ZhShell.Map);
        Register(ZhData.Map);
        Register(ZhTerminal.Map);
        Register(ZhProtocols.Map);

        _configured = settings.Current.Language;
        _isChinese = ResolveIsChinese(_configured);

        // Follow live settings changes (the settings page writes AppSettings.Language).
        settings.Changed += (_, _) => ApplySettings();
    }

    /// <summary>Raised when the effective language changes; the shell re-renders on it.</summary>
    public event EventHandler? Changed;

    /// <summary>Increments on every language change so the shell can remount its tree.</summary>
    public int Revision { get; private set; }

    /// <summary>True when Chinese (Simplified) is the active language.</summary>
    public bool IsChinese => _isChinese;

    /// <summary>Language currently in effect.</summary>
    public AppLanguage Effective => _isChinese ? AppLanguage.ChineseSimplified : AppLanguage.English;

    /// <summary>Registers a translation table (Chinese text keyed by the same keys as <see cref="Str"/>).</summary>
    public void Register(IReadOnlyDictionary<string, string> table)
    {
        foreach (var pair in table)
        {
            _chinese[pair.Key] = pair.Value;
        }
    }

    /// <summary>Subscription helper shaped for <c>UseExternalStore</c>.</summary>
    public Action Subscribe(Action listener)
    {
        EventHandler handler = (_, _) => listener();
        Changed += handler;
        return () => Changed -= handler;
    }

    /// <summary>Resolves a key to text, falling back to the English literal.</summary>
    public string Get(string key, string fallback) =>
        _isChinese && _chinese.TryGetValue(key, out var text) ? text : fallback;

    /// <summary>Formats a parameterised string.</summary>
    public string Format(string key, string fallback, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key, fallback), args);

    private void ApplySettings()
    {
        var next = _settings.Current.Language;
        var nextChinese = ResolveIsChinese(next);
        if (next == _configured && nextChinese == _isChinese)
        {
            return;
        }

        _configured = next;
        _isChinese = nextChinese;
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool ResolveIsChinese(AppLanguage language) => language switch
    {
        AppLanguage.ChineseSimplified => true,
        AppLanguage.English => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("zh", StringComparison.OrdinalIgnoreCase),
    };
}
