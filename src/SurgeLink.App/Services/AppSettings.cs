using Microsoft.Extensions.Logging;
using SurgeLink.App.Localization;
using SurgeLink.Core.Storage;
using SurgeLink.Terminal;

namespace SurgeLink.App.Services;

/// <summary>How the app picks light or dark.</summary>
public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// User preferences. Persisted as one JSON document; every consumer reads
/// <see cref="IAppSettingsStore.Current"/> and re-renders on
/// <see cref="IAppSettingsStore.Changed"/>.
/// </summary>
public sealed record AppSettings
{
    public static AppSettings Default { get; } = new();

    /// <summary>Light, dark, or follow Windows.</summary>
    public ThemeMode Theme { get; init; } = ThemeMode.System;

    /// <summary>UI language; <see cref="AppLanguage.System"/> follows Windows.</summary>
    public AppLanguage Language { get; init; } = AppLanguage.System;

    /// <summary>Monospace family for the terminal, empty for auto detection.</summary>
    public string TerminalFontFamily { get; init; } = string.Empty;

    /// <summary>Terminal font size in points.</summary>
    public double TerminalFontSize { get; init; } = 13;

    /// <summary>Terminal colour scheme name from <c>TerminalThemes.All</c>.</summary>
    public string TerminalPalette { get; init; } = "Campbell";

    /// <summary>Scrollback lines kept in memory per terminal.</summary>
    public int TerminalScrollbackLines { get; init; } = 10000;

    /// <summary>Ask before closing a tab whose session is still connected.</summary>
    public bool ConfirmCloseConnectedTab { get; init; } = true;

    /// <summary>Remote monitor sampling interval in seconds.</summary>
    public int MonitorIntervalSeconds { get; init; } = 2;

    /// <summary>Show dot files in the SFTP browser.</summary>
    public bool ShowHiddenFiles { get; init; } = true;

    /// <summary>Mask server addresses in the UI; hover reveals the original.</summary>
    public bool PrivacyMode { get; init; }
}

/// <summary>Reads and updates <see cref="AppSettings"/>; notifies the UI on change.</summary>
public interface IAppSettingsStore
{
    void UpdateTerminalFontSize(double size) => Update(settings => settings with { TerminalFontSize = size });
    void FlushPendingChanges() { }
    /// <summary>Current settings snapshot.</summary>
    AppSettings Current { get; }

    /// <summary>Raised after <see cref="Update"/> persists a new snapshot.</summary>
    event EventHandler? Changed;

    /// <summary>Subscription helper shaped for <c>UseExternalStore</c>.</summary>
    Action Subscribe(Action listener);

    /// <summary>Applies a mutation and persists it.</summary>
    void Update(Func<AppSettings, AppSettings> mutate);
}

/// <summary>JSON backed settings store.</summary>
public sealed class AppSettingsStore : IAppSettingsStore, IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger? _logger;
    private volatile AppSettings _current;
    private Timer? _fontSaveTimer;
    private double? _pendingFontSize;

    /// <summary>Creates the store and loads the existing settings file.</summary>
    public AppSettingsStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger;
        _current = Normalize(AtomicJsonFile.Read(path, AppSettings.Default));
    }

    /// <inheritdoc />
    public AppSettings Current => _current;

    public void UpdateTerminalFontSize(double size)
    {
        lock (_gate)
        {
            var next = Normalize(_current with { TerminalFontSize = size });
            if (next.TerminalFontSize == _current.TerminalFontSize) { return; }
            _pendingFontSize = next.TerminalFontSize;
            _current = next;
            _fontSaveTimer ??= new Timer(_ =>
            {
                try { FlushPendingChanges(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { _logger?.LogWarning(ex, "Could not save the terminal font size."); }
            });
            _fontSaveTimer.Change(300, Timeout.Infinite);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void FlushPendingChanges()
    {
        lock (_gate)
        {
            if (_pendingFontSize is not double size) { return; }
            AtomicJsonFile.WithExclusiveAccess(_path, () =>
            {
                var latest = Normalize(AtomicJsonFile.Read(_path, _current, tolerateErrors: false));
                AtomicJsonFile.Write(_path, latest with { TerminalFontSize = size });
                return true;
            });
            _pendingFontSize = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _fontSaveTimer?.Dispose();
            _fontSaveTimer = null;
            FlushPendingChanges();
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public Action Subscribe(Action listener)
    {
        EventHandler handler = (_, _) => listener();
        Changed += handler;
        return () => Changed -= handler;
    }

    /// <inheritdoc />
    public void Update(Func<AppSettings, AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var next = AtomicJsonFile.WithExclusiveAccess(_path, () =>
            {
                var latest = Normalize(AtomicJsonFile.Read(_path, _current, tolerateErrors: false));
                var basis = _pendingFontSize is double size ? latest with { TerminalFontSize = size } : latest;
                var updated = Normalize(mutate(basis));
                if (updated != latest || _pendingFontSize is not null) AtomicJsonFile.Write(_path, updated);
                return updated;
            });
            _pendingFontSize = null;
            if (next == _current)
            {
                return;
            }

            _current = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            Theme = Enum.IsDefined(settings.Theme) ? settings.Theme : ThemeMode.System,
            Language = Enum.IsDefined(settings.Language) ? settings.Language : AppLanguage.System,
            TerminalFontFamily = settings.TerminalFontFamily?.Trim() ?? string.Empty,
            TerminalFontSize = double.IsFinite(settings.TerminalFontSize)
                ? Math.Clamp(settings.TerminalFontSize, 9, 28) : AppSettings.Default.TerminalFontSize,
            TerminalScrollbackLines = Math.Clamp(settings.TerminalScrollbackLines, 1000, 100000),
            MonitorIntervalSeconds = Math.Clamp(settings.MonitorIntervalSeconds, 1, 30),
            TerminalPalette = TerminalThemes.All.FirstOrDefault(theme =>
                string.Equals(theme.Name, settings.TerminalPalette, StringComparison.OrdinalIgnoreCase)).Name
                ?? AppSettings.Default.TerminalPalette,
        };
    }
}
