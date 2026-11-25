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
        element.Set(control => Apply(control, original)).ToolTip(original);

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
            control.PointerEntered += (_, _) => { value.Hovered = true; Refresh(control, value); };
            control.PointerExited += (_, _) => { value.Hovered = false; Refresh(control, value); };
            control.Unloaded += (_, _) => { value.Hovered = false; Refresh(control, value); };
            return value;
        });
        state.Original = original;
        Refresh(text, state);
        ToolTipService.SetToolTip(text, original);
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
    }

    /// <summary>Reveal every private label when any part of its containing row is hovered.</summary>
    public static void ApplyGroup(FrameworkElement container, string name)
    {
        var group = Groups.GetValue(container, control =>
        {
            var value = new HoverGroup();
            control.PointerEntered += (_, _) => { value.Hovered = true; RefreshGroup(control, value); };
            control.PointerExited += (_, _) => { value.Hovered = false; RefreshGroup(control, value); };
            control.Unloaded += (_, _) => { value.Hovered = false; RefreshGroup(control, value); };
            control.Loaded += (_, _) => RefreshGroup(control, value);
            return value;
        });
        group.Name = name;
        RefreshGroup(container, group);
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
