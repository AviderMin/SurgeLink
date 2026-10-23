using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Services;
using SurgeLink.App.Styling;
using SurgeLink.Terminal;

namespace SurgeLink.App.Views.Terminal;

/// <summary>Font and colour-scheme half of <see cref="TerminalSurface"/>.</summary>
internal sealed partial class TerminalSurface
{
    private void ApplySettings(AppSettings settings, bool initial)
    {
        TerminalPalette palette = ResolvePalette(settings.TerminalPalette);
        string fontFamily = string.IsNullOrWhiteSpace(settings.TerminalFontFamily)
            ? AppTheme.MonospaceFamilyName
            : settings.TerminalFontFamily;
        double fontSize = settings.TerminalFontSize > 0 ? settings.TerminalFontSize : DefaultFontSize;

        bool paletteChanged = initial || _resolver is null || !ReferenceEquals(_resolver.Palette, palette);
        bool fontChanged = initial || _metrics is null
            || !string.Equals(_fontFamilyName, fontFamily, StringComparison.Ordinal)
            || Math.Abs(_fontSize - fontSize) > 0.001;

        if (!paletteChanged && !fontChanged)
        {
            return;
        }

        if (paletteChanged)
        {
            _resolver = new TerminalStyleResolver(palette);
            var brush = _resolver.Brush(_resolver.DefaultBackground);
            Background = brush;
            _root.Background = brush;
            // Terminal colours can differ from the shell theme. Keep the native
            // thumb legible on the terminal background in either colour scheme.
            var rgb = _resolver.DefaultBackground;
            var brightness = ((rgb >> 16) & 0xFF) * 299 + ((rgb >> 8) & 0xFF) * 587 + (rgb & 0xFF) * 114;
            _scrollBar.RequestedTheme = brightness < 128000 ? ElementTheme.Dark : ElementTheme.Light;
        }

        if (fontChanged)
        {
            _fontFamilyName = fontFamily;
            _fontSize = fontSize;
            _metrics = TerminalMetrics.Measure(fontFamily, fontSize);
            ApplyRunFont(_metrics);
            _logger?.LogInformation(
                "Terminal metrics: family={Family}, size={Size}, cell={CellWidth}x{CellHeight}",
                _metrics.FontFamilyName,
                _metrics.FontSize,
                Math.Round(_metrics.CellWidth, 3),
                Math.Round(_metrics.CellHeight, 3));
        }

        _allRowsDirty = true;
        UpdateGridSize();
        InvalidatePaint(force: true);
    }

    private static TerminalPalette ResolvePalette(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            foreach (var (themeName, palette) in TerminalThemes.All)
            {
                if (string.Equals(themeName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return palette;
                }
            }
        }

        return TerminalThemes.Campbell;
    }

    private void ApplyRunFont(TerminalMetrics metrics)
    {
        foreach (var row in _rowVisuals)
        {
            foreach (var run in row.Runs)
            {
                StyleRun(run, metrics);
            }
        }

        StyleRun(_cursorGlyph, metrics);
    }

    private static void StyleRun(TextBlock block, TerminalMetrics metrics)
    {
        block.FontFamily = metrics.FontFamily;
        block.FontSize = metrics.FontSize;
        // A fixed line box keeps every row exactly CellHeight tall, whatever glyphs it holds.
        block.LineHeight = metrics.CellHeight;
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }
}
