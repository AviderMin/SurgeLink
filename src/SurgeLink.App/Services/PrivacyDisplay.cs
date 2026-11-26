using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SurgeLink.App.Sessions;

namespace SurgeLink.App.Services;

/// <summary>Display-only address masking. Connection profiles are never modified.</summary>
internal static class PrivacyDisplay
{
    private sealed class Masker(WorkspaceSnapshot snapshot)
    {
        private readonly System.Text.RegularExpressions.Regex? _regex = AddressMasker.CreateMatcher(
            snapshot.Profiles.Select(profile => profile.Host)
                .Concat(snapshot.Tabs.Select(tab => tab.Profile.Host))
                .Concat(snapshot.Tabs.Select(tab => tab.Session.HostInfo.HostName ?? string.Empty)));

        internal string Apply(string text) => _regex?.Replace(text, "***") ?? text;
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
            control.PointerEntered += (_, _) => { value.Hovered = true; control.PasswordRevealMode = PasswordRevealMode.Visible; };
            control.PointerExited += (_, _) => { value.Hovered = false; control.PasswordRevealMode = PasswordRevealMode.Hidden; };
            control.Unloaded += (_, _) => { value.Hovered = false; control.PasswordRevealMode = PasswordRevealMode.Hidden; };
            return value;
        });
        input.PasswordRevealMode = state.Hovered ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
    }
}
