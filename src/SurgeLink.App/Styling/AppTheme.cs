using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Media;
using SurgeLink.Core.Sessions;

namespace SurgeLink.App.Styling;

/// <summary>
/// Design tokens for the shell. Everything visual is expressed through these so
/// the app follows Windows 11 spacing, radius and type ramp consistently.
/// </summary>
public static class AppTheme
{
    /// <summary>4px grid: extra small spacing.</summary>
    public const double SpacingXs = 4;

    /// <summary>4px grid: small spacing.</summary>
    public const double SpacingS = 8;

    /// <summary>4px grid: medium spacing.</summary>
    public const double SpacingM = 12;

    /// <summary>4px grid: large spacing.</summary>
    public const double SpacingL = 16;

    /// <summary>4px grid: extra large spacing.</summary>
    public const double SpacingXl = 24;

    /// <summary>Standard card corner radius.</summary>
    public const double CardRadius = 8;

    /// <summary>Control corner radius.</summary>
    public const double ControlRadius = 4;

    /// <summary>Sidebar width when expanded.</summary>
    public const double SidebarWidth = 300;

    /// <summary>Status bar height.</summary>
    public const double StatusBarHeight = 28;

    /// <summary>Header row height used by dialogs and settings cards.</summary>
    public const double RowMinHeight = 40;

    /// <summary>Monospace families offered in the terminal settings.</summary>
    public static IReadOnlyList<string> MonospaceCandidates { get; } = new[]
    {
        "Cascadia Mono",
        "Cascadia Code",
        "Consolas",
        "Courier New",
        "Lucida Console",
    };

    private static readonly Lazy<string> MonospaceName = new(DetectMonospace);

    private static readonly Lazy<FontFamily> MonospaceFont = new(() => new FontFamily(MonospaceName.Value));

    /// <summary>Name of the first available monospace font.</summary>
    public static string MonospaceFamilyName => MonospaceName.Value;

    /// <summary>Font family used for terminal text and inline code.</summary>
    public static FontFamily Monospace => MonospaceFont.Value;

    /// <summary>
    /// Resolves the terminal font family from a setting value. Empty means
    /// "auto detect"; unknown families fall back to the detected monospace face.
    /// </summary>
    public static FontFamily MonospaceFor(string? familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName) ||
            string.Equals(familyName, MonospaceFamilyName, StringComparison.OrdinalIgnoreCase))
        {
            return Monospace;
        }

        // FontFamily is a cheap wrapper; the terminal view caches metrics, not this.
        return new FontFamily(familyName!);
    }

    /// <summary>Accent colour that represents a connection state, High Contrast safe.</summary>
    public static ThemeRef StatusBrush(ConnectionState state) => state switch
    {
        ConnectionState.Connected => Theme.SystemSuccess,
        ConnectionState.Connecting or ConnectionState.Reconnecting => Theme.SystemCaution,
        ConnectionState.Disconnecting => Theme.SystemCaution,
        ConnectionState.Faulted => Theme.SystemCritical,
        _ => Theme.SystemNeutral,
    };

    /// <summary>Short human readable state used by the title and status bar.</summary>
    public static string StatusText(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "Connected",
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Reconnecting => "Reconnecting…",
        ConnectionState.Disconnecting => "Disconnecting…",
        ConnectionState.Disconnected => "Disconnected",
        ConnectionState.Faulted => "Connection failed",
        _ => state.ToString(),
    };

    /// <summary>Glyph that represents a connection state in the status bar.</summary>
    public static string StatusGlyph(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "\uE930",
        ConnectionState.Connecting or ConnectionState.Reconnecting => "\uE895",
        ConnectionState.Faulted => "\uEA39",
        _ => "\uE8C8",
    };

    private static string DetectMonospace()
    {
        // Cascadia Mono ships with Windows Terminal (per user), Consolas ships with Windows.
        var candidates = new[]
        {
            (Name: "Cascadia Mono", File: "CascadiaMono.ttf"),
            (Name: "Cascadia Code", File: "CascadiaCode.ttf"),
            (Name: "Consolas", File: "consola.ttf"),
        };

        var fontDirectories = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"),
        };

        foreach (var (name, file) in candidates)
        {
            foreach (var directory in fontDirectories)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, file)))
                    {
                        return name;
                    }
                }
                catch (IOException)
                {
                }
            }
        }

        return "Consolas";
    }
}
