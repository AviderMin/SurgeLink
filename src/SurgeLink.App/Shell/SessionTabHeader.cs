using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Shapes;
using SurgeLink.App.Components;
using SurgeLink.App.Localization;
using SurgeLink.App.Services;
using SurgeLink.Core.Sessions;

namespace SurgeLink.App.Shell;

/// <summary>Keeps tab header alignment independent of the session content alignment.</summary>
internal static class SessionTabHeader
{
    private sealed class Header
    {
        public Grid Layout { get; } = new() { VerticalAlignment = VerticalAlignment.Center };
        public Viewbox IconHost { get; } = new()
        {
            Width = 16,
            Height = 16,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        public TextBlock Title { get; } = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        public string? IconKey;

        // The fixed-size host reserves room for the dot in every state, so the
        // title and the tab close button never move when it appears.
        public Grid ActivityHost { get; } = new()
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        public Ellipse ActivityDot { get; } = (Ellipse)XamlReader.Load(
            "<Ellipse xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "Width=\"8\" Height=\"8\" Fill=\"{ThemeResource AccentFillColorDefaultBrush}\" " +
            "Visibility=\"Collapsed\" />");

        public FontIcon AgentIcon { get; } = new()
        {
            Glyph = "\uE99A",
            FontSize = 14,
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = true,
        };

        public Header()
        {
            ActivityHost.Children.Add(ActivityDot);
            Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(Title, 1);
            Grid.SetColumn(ActivityHost, 2);
            Grid.SetColumn(AgentIcon, 3);
            AutomationProperties.SetAccessibilityView(IconHost, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Layout.Children.Add(IconHost);
            Layout.Children.Add(Title);
            Layout.Children.Add(ActivityHost);
            Layout.Children.Add(AgentIcon);
        }
    }

    private static readonly ConditionalWeakTable<TabViewItem, Header> Headers = new();

    /// <summary>
    /// Applies the tab title, remote-system icon and status indicators. The
    /// activity dot is announced through the tab name and tooltip so assistive
    /// technology reports new output without exposing a separate focus stop.
    /// </summary>
    public static void Apply(TabViewItem item, string title, string? operatingSystem, bool agentControlled = false,
        bool hasActivity = false)
    {
        var header = Headers.GetValue(item, _ => new Header());
        PrivacyDisplay.Apply(header.Title, title);
        var key = RemoteSystemIcon.Resolve(operatingSystem);
        if (header.IconKey != key)
        {
            header.IconHost.Child = SystemIconPaths.Data.TryGetValue(key, out var path)
                // Render the complete geometry at its design size, then scale it.
                // PathIcon keeps its intrinsic geometry size and clips in a small host.
                ? (Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load(
                    "<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                    "Width=\"24\" Height=\"24\" Stretch=\"Uniform\" " +
                    "Fill=\"{ThemeResource TextFillColorPrimaryBrush}\" Data=\"" + path + "\" />")
                : new FontIcon { Glyph = "\uE774", FontSize = 16 };
            header.IconKey = key;
        }

        var agentStatus = agentControlled ? Str.T("shell.agent.tabControlled", "Agent is controlling this session") : string.Empty;
        var activityStatus = hasActivity ? Str.T("shell.tab.newOutput", "New output") : string.Empty;
        header.AgentIcon.Visibility = agentControlled ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(header.AgentIcon, agentStatus);
        AutomationProperties.SetAutomationId(header.AgentIcon, "SessionAgentIndicator");
        ToolTipService.SetToolTip(header.AgentIcon, agentStatus);
        header.ActivityDot.Visibility = hasActivity ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(header.ActivityDot, activityStatus);
        AutomationProperties.SetAutomationId(header.ActivityDot, "SessionActivityIndicator");
        ToolTipService.SetToolTip(header.ActivityDot, activityStatus);

        if (!ReferenceEquals(item.Header, header.Layout)) { item.Header = header.Layout; }

        var accessibleName = agentControlled
            ? Str.F("shell.agent.tabAccessible", "{0}, controlled by Agent", PrivacyDisplay.Mask(title))
            : PrivacyDisplay.Mask(title);
        if (hasActivity)
        {
            accessibleName = Str.F("shell.tab.accessible", "{0}, {1}", accessibleName, activityStatus);
        }

        AutomationProperties.SetName(item, accessibleName);
        AutomationProperties.SetHelpText(item, agentControlled && hasActivity
            ? agentStatus + "\n" + activityStatus
            : agentControlled ? agentStatus : activityStatus);
        var tooltip = PrivacyDisplay.Mask(title);
        if (agentControlled) { tooltip += "\n" + agentStatus; }
        if (hasActivity) { tooltip += "\n" + activityStatus; }
        ToolTipService.SetToolTip(item, tooltip);
    }
}
