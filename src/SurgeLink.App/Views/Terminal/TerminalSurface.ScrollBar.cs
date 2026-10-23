using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace SurgeLink.App.Views.Terminal;

/// <summary>Hover visibility for the terminal's standalone native scrollbar.</summary>
internal sealed partial class TerminalSurface
{
    private bool _pointerOverTerminal;
    private bool _scrollBarInteracting;

    private void HookScrollBarHover()
    {
        _scrollBar.Loaded += (_, _) => SquareScrollBarTrack(_scrollBar);
        PointerEntered += (_, _) =>
        {
            _pointerOverTerminal = true;
            UpdateScrollBarVisibility();
        };
        PointerExited += (_, _) =>
        {
            _pointerOverTerminal = false;
            UpdateScrollBarVisibility();
        };
        _scrollBar.PointerEntered += (_, _) => UpdateScrollBarVisibility();
        _scrollBar.PointerExited += (_, _) => UpdateScrollBarVisibility();
        _scrollBar.IsEnabledChanged += (_, _) => UpdateScrollBarVisibility();

        // Native thumbs and repeat buttons handle their pointer events. Observe
        // those too so dragging beyond the terminal keeps the scrollbar visible.
        _scrollBar.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            _scrollBarInteracting = true;
            UpdateScrollBarVisibility();
        }), true);
        var endInteraction = new PointerEventHandler((_, _) =>
        {
            _scrollBarInteracting = false;
            UpdateScrollBarVisibility();
        });
        _scrollBar.AddHandler(PointerReleasedEvent, endInteraction, true);
        _scrollBar.AddHandler(PointerCaptureLostEvent, endInteraction, true);
        _scrollBar.AddHandler(PointerCanceledEvent, endInteraction, true);
    }

    private static void SquareScrollBarTrack(DependencyObject element)
    {
        // WinUI shares CornerRadius between track and thumb. Override only the
        // track shapes after the template loads; keep the thumb's native radius.
        if (element is Microsoft.UI.Xaml.Shapes.Rectangle
            { Name: "VerticalTrackRect" or "HorizontalTrackRect" } track)
        {
            track.RadiusX = 0;
            track.RadiusY = 0;
        }
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element); index++)
        {
            SquareScrollBarTrack(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, index));
        }
    }

    private void UpdateScrollBarVisibility()
    {
        var visible = _scrollBar.IsEnabled && (_pointerOverTerminal || _scrollBarInteracting);
        _scrollBar.Opacity = visible ? 1 : 0;
        // Expand the native track and thumb on terminal hover. A standalone
        // ScrollBar has no ScrollViewer to activate this state before a click.
        VisualStateManager.GoToState(_scrollBar,
            visible ? "ExpandedWithoutAnimation" : "CollapsedWithoutAnimation", false);
    }
}
