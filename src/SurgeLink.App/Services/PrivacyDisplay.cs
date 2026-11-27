using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SurgeLink.App.Sessions;
using SurgeLink.Core.Protocols;
using SurgeLink.Protocols.Ssh;

namespace SurgeLink.App.Services;

/// <summary>Display-only address masking. Connection profiles are never modified.</summary>
internal static class PrivacyDisplay
{
    private sealed class Masker(WorkspaceSnapshot snapshot)
    {
        private readonly System.Text.RegularExpressions.Regex? _regex = AddressMasker.CreateMatcher(ExtractHosts(snapshot));

        internal string Apply(string text) => _regex?.Replace(text, "***") ?? text;

        private static IEnumerable<string> ExtractHosts(WorkspaceSnapshot snapshot)
        {
            foreach (var profile in snapshot.Profiles)
            {
                yield return profile.Host;
                var stripped = StripBrackets(profile.Host);
                if (!string.Equals(stripped, profile.Host, StringComparison.Ordinal)) yield return stripped;
                foreach (var extra in ExtractOptionHosts(profile.Options)) yield return extra;
            }
            foreach (var tab in snapshot.Tabs)
            {
                yield return tab.Profile.Host;
                var stripped = StripBrackets(tab.Profile.Host);
                if (!string.Equals(stripped, tab.Profile.Host, StringComparison.Ordinal)) yield return stripped;
                if (tab.Session.HostInfo.HostName is { } hostName) yield return hostName;
                foreach (var extra in ExtractOptionHosts(tab.Profile.Options)) yield return extra;
            }
        }

        private static IEnumerable<string> ExtractOptionHosts(IReadOnlyDictionary<string, string> options)
        {
            if (options.TryGetValue("proxyJump", out var jump) && !string.IsNullOrWhiteSpace(jump))
            {
                foreach (var hop in jump.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var host = ExtractJumpHost(hop);
                    if (!IsCommonLoopbackOrWildcard(host)) yield return host;
                }
            }

            if (options.TryGetValue("portForwards", out var forwards) && !string.IsNullOrWhiteSpace(forwards))
            {
                foreach (var entry in forwards.Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = SplitOutsideBrackets(entry);
                    if (parts.Count < 2) continue;
                    var prefix = parts[0].ToUpperInvariant();
                    if (prefix is "L" or "R")
                    {
                        if (parts.Count == 4 && !IsCommonLoopbackOrWildcard(parts[2])) yield return StripBrackets(parts[2]);
                        else if (parts.Count == 5)
                        {
                            if (!IsCommonLoopbackOrWildcard(parts[1])) yield return StripBrackets(parts[1]);
                            if (!IsCommonLoopbackOrWildcard(parts[3])) yield return StripBrackets(parts[3]);
                        }
                    }
                    else if (prefix == "D" && parts.Count == 3 && !IsCommonLoopbackOrWildcard(parts[1]))
                    {
                        yield return StripBrackets(parts[1]);
                    }
                }
            }
        }

        private static string ExtractJumpHost(string text)
        {
            try { return SshJumpTarget.Parse(text, string.Empty).Host; }
            catch (ProtocolException)
            {
                // The rejected setting can also appear verbatim in a connection error.
                var host = text[(text.LastIndexOf('@') + 1)..].Trim();
                if (host.StartsWith('['))
                {
                    var close = host.IndexOf(']');
                    return close > 1 ? host[1..close] : string.Empty;
                }
                var colon = host.IndexOf(':');
                return colon >= 0 && colon == host.LastIndexOf(':') ? host[..colon].Trim() : host;
            }
        }

        private static List<string> SplitOutsideBrackets(string text)
        {
            var result = new List<string>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                switch (text[i])
                {
                    case '[': depth++; break;
                    case ']' when depth > 0: depth--; break;
                    case ':' when depth == 0:
                        result.Add(text[start..i].Trim());
                        start = i + 1;
                        break;
                }
            }
            result.Add(text[start..].Trim());
            return result;
        }

        private static string StripBrackets(string value)
        {
            var trimmed = value.Trim();
            return trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2
                ? trimmed[1..^1]
                : trimmed;
        }

        private static bool IsCommonLoopbackOrWildcard(string rawHost)
        {
            var host = StripBrackets(rawHost);
            return string.IsNullOrWhiteSpace(host)
                || host is "*" or "0.0.0.0" or "::" or "::1" or "127.0.0.1"
                || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorkspaceSnapshot, Masker> Matchers = new();

    public static string Mask(string text)
    {
        var services = AppServices.Current;
        if (services is null || !services.Settings.Current.PrivacyMode || string.IsNullOrEmpty(text)) return text;
        return Matchers.GetValue(services.Workspace.Snapshot, static snapshot => new(snapshot)).Apply(text);
    }

    public static TextBlockElement Private(this TextBlockElement element, string original) =>
        element.Set(control => Apply(control, original)).OnUnmountAdd(control => Detach((TextBlock)control));

    private sealed class HoverState
    {
        public string Original = string.Empty;
        public bool Hovered;
        public HoverGroup? Group;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, HoverState> States = new();

    public static void Apply(TextBlock text, string original)
    {
        var state = States.GetValue(text, control =>
        {
            var value = new HoverState();
            control.PointerEntered += OnLabelEntered;
            control.PointerExited += OnLabelExited;
            control.Unloaded += OnLabelExited;
            return value;
        });
        state.Original = original;
        Refresh(text, state);
    }

    private static void OnLabelEntered(object sender, RoutedEventArgs args) => SetLabelHover((TextBlock)sender, true);
    private static void OnLabelExited(object sender, RoutedEventArgs args) => SetLabelHover((TextBlock)sender, false);

    private static void SetLabelHover(TextBlock text, bool hovered)
    {
        if (!States.TryGetValue(text, out var state)) return;
        state.Hovered = hovered;
        Refresh(text, state);
    }

    private static void Detach(TextBlock text)
    {
        if (!States.Remove(text)) return;
        text.PointerEntered -= OnLabelEntered;
        text.PointerExited -= OnLabelExited;
        text.Unloaded -= OnLabelExited;
        text.ClearValue(AutomationProperties.NameProperty);
        text.ClearValue(ToolTipService.ToolTipProperty);
    }


    private sealed class HoverGroup
    {
        public bool Hovered;
        public string Name = string.Empty;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, HoverGroup> Groups = new();

    private static void Refresh(TextBlock text, HoverState state)
    {
        var revealed = state.Group?.Hovered ?? state.Hovered;
        text.Text = revealed ? state.Original : Mask(state.Original);
        AutomationProperties.SetName(text, text.Text);
        ToolTipService.SetToolTip(text, text.Text);
    }

    /// <summary>Reveal every private label when any part of its containing row is hovered.</summary>
    public static void ApplyGroup(FrameworkElement container, string name)
    {
        var group = Groups.GetValue(container, control =>
        {
            var value = new HoverGroup();
            control.PointerEntered += OnGroupEntered;
            control.PointerExited += OnGroupExited;
            control.Unloaded += OnGroupExited;
            control.Loaded += OnGroupExited;
            return value;
        });
        group.Name = name;
        RefreshGroup(container, group);
    }

    private static void OnGroupEntered(object sender, RoutedEventArgs args) => SetGroupHover((FrameworkElement)sender, true);
    private static void OnGroupExited(object sender, RoutedEventArgs args) => SetGroupHover((FrameworkElement)sender, false);

    private static void SetGroupHover(FrameworkElement container, bool hovered)
    {
        if (!Groups.TryGetValue(container, out var group)) return;
        group.Hovered = hovered;
        RefreshGroup(container, group);
    }

    public static void DetachGroup(FrameworkElement container)
    {
        if (!Groups.Remove(container)) return;
        container.PointerEntered -= OnGroupEntered;
        container.PointerExited -= OnGroupExited;
        container.Unloaded -= OnGroupExited;
        container.Loaded -= OnGroupExited;
        container.ClearValue(AutomationProperties.NameProperty);
    }

    private static void RefreshGroup(FrameworkElement container, HoverGroup group)
    {
        AutomationProperties.SetName(container, group.Hovered ? group.Name : Mask(group.Name));
        Visit(container);
        void Visit(DependencyObject parent)
        {
            if (parent is TextBlock text && States.TryGetValue(text, out var state))
            {
                state.Group = group;
                Refresh(text, state);
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                Visit(VisualTreeHelper.GetChild(parent, index));
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PasswordBox, HoverState> Inputs = new();

    public static void HideInput(PasswordBox input)
    {
        var state = Inputs.GetValue(input, control =>
        {
            var value = new HoverState();
            control.PointerEntered += OnInputEntered;
            control.PointerExited += OnInputExited;
            control.Unloaded += OnInputExited;
            return value;
        });
        input.PasswordRevealMode = state.Hovered ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
    }

    public static void DetachInput(PasswordBox input)
    {
        if (!Inputs.Remove(input)) return;
        input.PointerEntered -= OnInputEntered;
        input.PointerExited -= OnInputExited;
        input.Unloaded -= OnInputExited;
        input.PasswordRevealMode = PasswordRevealMode.Hidden;
    }

    private static void OnInputEntered(object sender, RoutedEventArgs args) => SetInputHover((PasswordBox)sender, true);
    private static void OnInputExited(object sender, RoutedEventArgs args) => SetInputHover((PasswordBox)sender, false);

    private static void SetInputHover(PasswordBox input, bool hovered)
    {
        if (!Inputs.TryGetValue(input, out var state)) return;
        state.Hovered = hovered;
        input.PasswordRevealMode = hovered ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
    }
}
