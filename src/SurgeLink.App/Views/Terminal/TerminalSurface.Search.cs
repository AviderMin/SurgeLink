using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SurgeLink.App.Localization;
using SurgeLink.Terminal;
using Windows.System;
using Windows.UI;

namespace SurgeLink.App.Views.Terminal;

/// <summary>
/// Search half of <see cref="TerminalSurface"/>: the in-terminal find bar, the
/// match model and the highlighting contract consumed by the painter. Search runs
/// against the emulator under its normal lock so it sees the same cells as the
/// renderer and never blocks output for longer than one snapshot.
/// </summary>
internal sealed partial class TerminalSurface
{
    private const int MaxSearchMatches = 1000;

    private readonly Border _searchBar = new();
    private readonly TextBox _searchBox = new();
    private readonly TextBlock _searchStatus = new();
    private readonly ToggleButton _searchCase = new();
    private readonly Button _searchPrevious = new();
    private readonly Button _searchNext = new();
    private readonly Button _searchClose = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _searchRefreshTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _searchInputTimer;

    private string _searchQuery = string.Empty;
    private bool _searchMatchCase;
    private IReadOnlyList<TerminalSearchMatch> _searchMatches = Array.Empty<TerminalSearchMatch>();
    private int _searchIndex = -1;
    private bool _searchTruncated;

    /// <summary>True while the find bar is visible.</summary>
    private bool IsSearchVisible => _searchBar.Visibility == Visibility.Visible;

    /// <summary>True when a routed event originated anywhere in the find bar subtree.</summary>
    private bool IsSearchBarEvent(object? originalSource)
    {
        for (var source = originalSource as DependencyObject;
             source is not null && source != this;
             source = VisualTreeHelper.GetParent(source))
        {
            if (ReferenceEquals(source, _searchBar)) { return true; }
        }

        return false;
    }

    private void InitializeSearch()
    {
        _searchBox.Width = 220;
        _searchBox.FontSize = 12;
        _searchBox.Padding = new Thickness(8, 4, 8, 4);
        _searchBox.IsSpellCheckEnabled = false;
        _searchBox.TextWrapping = TextWrapping.NoWrap;
        _searchBox.PlaceholderText = Str.T("terminal.find.placeholder", "Find in terminal");
        AutomationProperties.SetAutomationId(_searchBox, "TerminalSearchBox");
        AutomationProperties.SetName(_searchBox, Str.T("terminal.find.placeholder", "Find in terminal"));
        _searchBox.TextChanged += (_, _) => OnSearchTextChanged();
        _searchBox.KeyDown += OnSearchBoxKeyDown;

        _searchStatus.FontSize = 12;
        _searchStatus.MinWidth = 64;
        _searchStatus.Margin = new Thickness(6, 0, 4, 0);
        _searchStatus.VerticalAlignment = VerticalAlignment.Center;
        _searchStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetAutomationId(_searchStatus, "TerminalSearchStatus");

        _searchCase.Content = "Aa";
        _searchCase.Width = 32;
        _searchCase.Height = 28;
        _searchCase.Padding = new Thickness(0);
        _searchCase.FontSize = 12;
        _searchCase.VerticalAlignment = VerticalAlignment.Center;
        _searchCase.IsChecked = false;
        ToolTipService.SetToolTip(_searchCase, Str.T("terminal.find.case", "Match case"));
        AutomationProperties.SetName(_searchCase, Str.T("terminal.find.case", "Match case"));
        AutomationProperties.SetAutomationId(_searchCase, "TerminalSearchCase");
        _searchCase.Checked += (_, _) => { _searchMatchCase = true; RefreshSearch(resetCurrent: false, ensureVisible: false); };
        _searchCase.Unchecked += (_, _) => { _searchMatchCase = false; RefreshSearch(resetCurrent: false, ensureVisible: false); };

        ConfigureSearchButton(_searchPrevious, "\uE70E", Str.T("terminal.find.previous", "Previous match (Shift+Enter)"),
            "TerminalSearchPrevious", () => NavigateSearch(-1));
        ConfigureSearchButton(_searchNext, "\uE70D", Str.T("terminal.find.next", "Next match (Enter)"),
            "TerminalSearchNext", () => NavigateSearch(1));
        ConfigureSearchButton(_searchClose, "\uE711", Str.T("terminal.find.close", "Close search (Esc)"),
            "TerminalSearchClose", CloseSearch);

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(_searchBox);
        panel.Children.Add(_searchStatus);
        panel.Children.Add(_searchCase);
        panel.Children.Add(_searchPrevious);
        panel.Children.Add(_searchNext);
        panel.Children.Add(_searchClose);

        _searchBar.Child = panel;
        _searchBar.Visibility = Visibility.Collapsed;
        _searchBar.HorizontalAlignment = HorizontalAlignment.Right;
        _searchBar.VerticalAlignment = VerticalAlignment.Top;
        _searchBar.Margin = new Thickness(0, 8, ScrollBarWidth + 8, 0);
        _searchBar.Padding = new Thickness(4, 2, 4, 2);
        _searchBar.CornerRadius = new CornerRadius(6);
        _searchBar.BorderThickness = new Thickness(1);
        Grid.SetColumnSpan(_searchBar, 2);
        _root.Children.Add(_searchBar);

        Microsoft.UI.Dispatching.DispatcherQueueTimer? timer = DispatcherQueue?.CreateTimer();
        if (timer is not null)
        {
            timer.Interval = TimeSpan.FromMilliseconds(250);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (IsSearchVisible && !string.IsNullOrEmpty(_searchQuery))
                {
                    // Output updates must not move the user's viewport; only typing
                    // and explicit navigation scroll to a match.
                    RefreshSearch(resetCurrent: false, ensureVisible: false);
                }
            };
            _searchRefreshTimer = timer;
        }

        Microsoft.UI.Dispatching.DispatcherQueueTimer? inputTimer = DispatcherQueue?.CreateTimer();
        if (inputTimer is not null)
        {
            // Typing should not rescan a large scrollback on every keystroke. The
            // short debounce keeps interaction responsive while still searching
            // immediately after the user pauses.
            inputTimer.Interval = TimeSpan.FromMilliseconds(150);
            inputTimer.Tick += (_, _) =>
            {
                inputTimer.Stop();
                RefreshSearch(resetCurrent: true);
            };
            _searchInputTimer = inputTimer;
        }

        ApplySearchLocalization();
    }

    /// <summary>Re-applies text and accessibility names after a runtime language change.</summary>
    private void ApplySearchLocalization()
    {
        string placeholder = Str.T("terminal.find.placeholder", "Find in terminal");
        if (!string.Equals(_searchBox.PlaceholderText, placeholder, StringComparison.Ordinal))
        {
            _searchBox.PlaceholderText = placeholder;
        }

        AutomationProperties.SetName(_searchBox, placeholder);

        string caseName = Str.T("terminal.find.case", "Match case");
        AutomationProperties.SetName(_searchCase, caseName);
        ToolTipService.SetToolTip(_searchCase, caseName);

        ApplySearchButtonLocalization(_searchPrevious, "terminal.find.previous", "Previous match (Shift+Enter)");
        ApplySearchButtonLocalization(_searchNext, "terminal.find.next", "Next match (Enter)");
        ApplySearchButtonLocalization(_searchClose, "terminal.find.close", "Close search (Esc)");
        if (IsSearchVisible)
        {
            UpdateSearchStatus();
        }
    }

    private static void ApplySearchButtonLocalization(Button button, string key, string fallback)
    {
        string name = Str.T(key, fallback);
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
    }

    private static void ConfigureSearchButton(Button button, string glyph, string name, string automationId, Action action)
    {
        button.Width = 28;
        button.Height = 28;
        button.Padding = new Thickness(0);
        button.Content = new FontIcon { Glyph = glyph, FontSize = 12 };
        button.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) => action();
    }

    /// <summary>Re-applies terminal palette colours to the find bar.</summary>
    private void StyleSearchBar()
    {
        if (_resolver is null)
        {
            return;
        }

        _searchBar.Background = _resolver.Brush(_resolver.DefaultBackground);
        uint foreground = _resolver.DefaultForeground;
        _searchBar.BorderBrush = new SolidColorBrush(Color.FromArgb(
            96,
            (byte)((foreground >> 16) & 0xFF),
            (byte)((foreground >> 8) & 0xFF),
            (byte)(foreground & 0xFF)));
        _searchStatus.Foreground = _resolver.Brush(foreground);

        // Match the scrollbar rule: controls follow the terminal background, not
        // the shell theme, so a light palette inside a dark window stays readable.
        var rgb = _resolver.DefaultBackground;
        int brightness = (((int)((rgb >> 16) & 0xFF)) * 299)
            + (((int)((rgb >> 8) & 0xFF)) * 587)
            + (((int)(rgb & 0xFF)) * 114);
        _searchBar.RequestedTheme = brightness < 128000 ? ElementTheme.Dark : ElementTheme.Light;
    }

    /// <summary>Opens the find bar, seeding it from a single-line selection when present.</summary>
    private void OpenSearch()
    {
        string selected = SelectedTextForSearch();
        if (!string.IsNullOrEmpty(selected) && selected.IndexOf('\n') < 0 && selected.Length <= 200)
        {
            _searchBox.Text = selected;
        }

        _searchBar.Visibility = Visibility.Visible;
        _searchBox.Focus(FocusState.Programmatic);
        _searchBox.SelectAll();
        _searchInputTimer?.Stop();
        if (!string.IsNullOrEmpty(_searchBox.Text))
        {
            RefreshSearch(resetCurrent: true);
        }
        else
        {
            UpdateSearchStatus();
        }
    }

    /// <summary>Closes the find bar and drops highlights without touching terminal content.</summary>
    private void CloseSearch()
    {
        _searchRefreshTimer?.Stop();
        _searchInputTimer?.Stop();
        _searchBar.Visibility = Visibility.Collapsed;
        _searchMatches = Array.Empty<TerminalSearchMatch>();
        _searchIndex = -1;
        _searchTruncated = false;
        UpdateSearchStatus();
        InvalidatePaint(force: true);
        Focus(FocusState.Programmatic);
    }

    private string SelectedTextForSearch()
    {
        TerminalSelectionRange range = TerminalSelectionRange.FromAnchors(_selectionAnchor, _selectionCaret);
        if (!range.HasSelection)
        {
            return string.Empty;
        }

        var emulator = _bridge.Emulator;
        lock (emulator.SyncRoot)
        {
            return emulator.GetSelectionText(new TerminalSelection(range.First, range.Last));
        }
    }

    private void OnSearchTextChanged()
    {
        _searchQuery = _searchBox.Text ?? string.Empty;
        if (_searchInputTimer is { } timer)
        {
            timer.Stop();
            timer.Start();
            return;
        }

        RefreshSearch(resetCurrent: true);
    }

    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = IsKeyDown(VirtualKey.Shift);
        if (e.Key == VirtualKey.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter || e.Key == VirtualKey.F3)
        {
            // Flush a pending debounce so Enter always acts on the current query.
            if (_searchInputTimer is { IsRunning: true } timer)
            {
                timer.Stop();
                RefreshSearch(resetCurrent: true);
            }

            NavigateSearch(shift ? -1 : 1);
            e.Handled = true;
        }
    }

    private void RefreshSearch(bool resetCurrent, bool ensureVisible = true)
    {
        if (!IsSearchVisible || string.IsNullOrEmpty(_searchQuery))
        {
            _searchMatches = Array.Empty<TerminalSearchMatch>();
            _searchIndex = -1;
            _searchTruncated = false;
            UpdateSearchStatus();
            InvalidatePaint(force: true);
            return;
        }

        var emulator = _bridge.Emulator;
        IReadOnlyList<TerminalSearchMatch> found;
        lock (emulator.SyncRoot)
        {
            // One extra result distinguishes "exactly at the cap" from "more than
            // the cap" without rescanning the scrollback.
            found = emulator.FindText(_searchQuery, _searchMatchCase, MaxSearchMatches + 1);
        }

        _searchTruncated = found.Count > MaxSearchMatches;
        _searchMatches = _searchTruncated
            ? found.Take(MaxSearchMatches).ToArray()
            : found;

        if (_searchMatches.Count == 0)
        {
            _searchIndex = -1;
            UpdateSearchStatus();
            InvalidatePaint(force: true);
            return;
        }

        if (resetCurrent)
        {
            int top;
            lock (emulator.SyncRoot)
            {
                top = emulator.Modes.AlternateScreen
                    ? emulator.ScrollbackLength
                    : emulator.ScrollbackLength - emulator.ViewportOffset;
            }

            int index = 0;
            while (index < _searchMatches.Count && _searchMatches[index].Line < top)
            {
                index++;
            }

            _searchIndex = index < _searchMatches.Count ? index : 0;
        }
        else if (_searchIndex < 0 || _searchIndex >= _searchMatches.Count)
        {
            _searchIndex = 0;
        }

        if (ensureVisible)
        {
            EnsureSearchVisible(_searchMatches[_searchIndex]);
        }

        UpdateSearchStatus();
        InvalidatePaint(force: true);
    }

    private void NavigateSearch(int delta)
    {
        if (_searchMatches.Count == 0)
        {
            return;
        }

        _searchIndex = ((_searchIndex + delta) % _searchMatches.Count + _searchMatches.Count) % _searchMatches.Count;
        EnsureSearchVisible(_searchMatches[_searchIndex]);
        UpdateSearchStatus();
        InvalidatePaint(force: true);
    }

    private void EnsureSearchVisible(TerminalSearchMatch match)
    {
        var emulator = _bridge.Emulator;
        int rows;
        int scrollback;
        int offset;
        lock (emulator.SyncRoot)
        {
            rows = emulator.Rows;
            scrollback = emulator.ScrollbackLength;
            offset = emulator.ViewportOffset;
        }

        int row = match.Line - (scrollback - offset);
        if (row >= 0 && row < rows)
        {
            return;
        }

        if (match.Line >= scrollback)
        {
            // A live-viewport match that is currently scrolled out of sight.
            if (offset != 0)
            {
                ScrollHistory(-offset);
            }

            return;
        }

        int targetRow = Math.Clamp(rows / 3, 0, Math.Max(0, rows - 1));
        int desired = scrollback - match.Line + targetRow;
        int delta = Math.Clamp(desired, 0, scrollback) - offset;
        if (delta != 0)
        {
            ScrollHistory(delta);
        }
    }

    private void UpdateSearchStatus()
    {
        if (!IsSearchVisible || string.IsNullOrEmpty(_searchQuery))
        {
            SetSearchStatus(string.Empty);
            return;
        }

        if (_searchMatches.Count == 0)
        {
            SetSearchStatus(Str.T("terminal.find.none", "No matches"));
            return;
        }

        SetSearchStatus(_searchTruncated
            ? Str.F("terminal.find.positionCapped", "{0} of first {1} matches", _searchIndex + 1, _searchMatches.Count)
            : Str.F("terminal.find.position", "{0} of {1}", _searchIndex + 1, _searchMatches.Count));
    }

    private void SetSearchStatus(string text)
    {
        if (string.Equals(_searchStatus.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        _searchStatus.Text = text;
        AutomationProperties.SetName(_searchStatus, text);
    }

    /// <summary>Builds viewport-row search spans for the current paint pass.</summary>
    private Dictionary<int, List<TerminalSearchSpan>>? BuildSearchHighlights(ITerminalEmulator emulator, int visibleRows)
    {
        if (!IsSearchVisible || _searchMatches.Count == 0)
        {
            return null;
        }

        int top = emulator.Modes.AlternateScreen
            ? emulator.ScrollbackLength
            : emulator.ScrollbackLength - emulator.ViewportOffset;
        Dictionary<int, List<TerminalSearchSpan>>? rows = null;
        for (int index = 0; index < _searchMatches.Count; index++)
        {
            TerminalSearchMatch match = _searchMatches[index];
            int row = match.Line - top;
            if (row < 0 || row >= visibleRows)
            {
                continue;
            }

            rows ??= new Dictionary<int, List<TerminalSearchSpan>>();
            if (!rows.TryGetValue(row, out List<TerminalSearchSpan>? spans))
            {
                spans = new List<TerminalSearchSpan>(2);
                rows[row] = spans;
            }

            spans.Add(new TerminalSearchSpan(match.StartColumn, match.EndColumn, index == _searchIndex));
        }

        return rows;
    }
}
