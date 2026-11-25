using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Services;
using SurgeLink.App.Styling;
using SurgeLink.Terminal;
using Windows.UI.Text;

namespace SurgeLink.App.Views.Terminal;

/// <summary>Font and colour-scheme half of <see cref="TerminalSurface"/>.</summary>
internal sealed partial class TerminalSurface
{
    private void ApplySettings(AppSettings settings, bool initial)
    {
        TerminalPalette palette = _bridge.Emulator.Palette;
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
            ApplyPalette(palette);
        }

        if (fontChanged)
        {
            _fontFamilyName = fontFamily;
            _fontSize = fontSize;
            _metrics = TerminalMetrics.Measure(fontFamily, fontSize);
            if (_loaded) ApplyRunFont(_metrics);
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

    private TerminalStyleResolver ApplyPalette(TerminalPalette palette)
    {
        var resolver = _resolver = new TerminalStyleResolver(palette);
        var brush = resolver.Brush(resolver.DefaultBackground);
        Background = brush;
        _root.Background = brush;
        StyleSearchBar();
        // Terminal colours can differ from the shell theme. Keep the native
        // thumb legible on the terminal background in either colour scheme.
        var rgb = resolver.DefaultBackground;
        var brightness = ((rgb >> 16) & 0xFF) * 299 + ((rgb >> 8) & 0xFF) * 587 + (rgb & 0xFF) * 114;
        _scrollBar.RequestedTheme = brightness < 128000 ? ElementTheme.Dark : ElementTheme.Light;
        return resolver;
    }

    private void ApplyRunFont(TerminalMetrics metrics)
    {
        DisposeRunFormats();
        _formatRegular = CreateCanvasTextFormat(metrics, FontWeights.Normal, FontStyle.Normal);
        _formatBold = CreateCanvasTextFormat(metrics, FontWeights.SemiBold, FontStyle.Normal);
        _formatItalic = CreateCanvasTextFormat(metrics, FontWeights.Normal, FontStyle.Italic);
        _formatBoldItalic = CreateCanvasTextFormat(metrics, FontWeights.SemiBold, FontStyle.Italic);

        StyleRun(_cursorGlyph, metrics);
    }

    private void DisposeRunFormats()
    {
        _formatRegular?.Dispose();
        _formatBold?.Dispose();
        _formatItalic?.Dispose();
        _formatBoldItalic?.Dispose();
        _formatRegular = _formatBold = _formatItalic = _formatBoldItalic = null;
    }

    private static CanvasTextFormat CreateCanvasTextFormat(TerminalMetrics metrics, FontWeight weight, FontStyle style) => new()
    {
        FontFamily = metrics.FontFamilyName,
        FontSize = (float)metrics.FontSize,
        FontWeight = weight,
        FontStyle = style,
        WordWrapping = CanvasWordWrapping.NoWrap,
    };

    private static void StyleRun(TextBlock block, TerminalMetrics metrics)
    {
        block.FontFamily = metrics.FontFamily;
        block.FontSize = metrics.FontSize;
        // A fixed line box keeps every row exactly CellHeight tall, whatever glyphs it holds.
        block.LineHeight = metrics.CellHeight;
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }
}
