using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SurgeLink.App.Shell;

/// <summary>
/// Gives the native <see cref="ContentDialog"/> buttons deterministic UI
/// Automation identities. The buttons only exist inside the dialog template, so
/// they are located when the dialog opens and named once per dialog instance.
/// Reactor pools dialog controls, so the prefix is re-read from
/// <see cref="FrameworkElement.Tag"/> on every open.
/// </summary>
internal static class DialogAutomation
{
    private static readonly ConditionalWeakTable<ContentDialog, object> Wired = new();

    /// <summary>
    /// Returns a configurator for <c>ContentDialog.Set(...)</c>. Button ids are
    /// <c>{prefix}Primary</c>, <c>{prefix}Secondary</c> and <c>{prefix}Close</c>.
    /// </summary>
    public static Action<ContentDialog> Tag(string prefix) => dialog =>
    {
        dialog.Tag = prefix;
        AutomationProperties.SetAutomationId(dialog, prefix + "Dialog");

        // Set() also runs on every re-render. The dialog template can rebuild
        // its command-space buttons when the content changes (for example an
        // inline validation InfoBar appears), so re-apply the ids here too.
        TagButtons(dialog, prefix);

        if (Wired.TryGetValue(dialog, out _))
        {
            return;
        }

        Wired.Add(dialog, prefix);
        dialog.Opened += (sender, _) =>
        {
            // The command-space buttons can still be absent during the first
            // Opened callback (the template is finishing realization). Tag once
            // immediately and once on the next dispatcher turn, idempotently.
            TagButtons(sender, sender.Tag as string ?? prefix);
            sender.DispatcherQueue?.TryEnqueue(() => TagButtons(sender, sender.Tag as string ?? prefix));
        };
    };

    private static void TagButtons(ContentDialog dialog, string prefix)
    {
        // The dialog's own buttons live in the "CommandSpace" panel of the
        // template. Searching the whole dialog would also hit the close button of
        // an InfoBar inside the content, so the walk is scoped.
        var scope = Find<FrameworkElement>(dialog, "CommandSpace") ?? (DependencyObject)dialog;
        TagButton(scope, "PrimaryButton", prefix + "Primary");
        TagButton(scope, "SecondaryButton", prefix + "Secondary");
        TagButton(scope, "CloseButton", prefix + "Close");
    }

    private static void TagButton(DependencyObject root, string templateName, string automationId)
    {
        var button = Find<Button>(root, templateName);
        if (button is null)
        {
            return;
        }

        AutomationProperties.SetAutomationId(button, automationId);
        if (button.Content is string text && !string.IsNullOrWhiteSpace(text))
        {
            // Keep the visible label as the accessible name so screen readers and
            // UI Automation (the verification harness) see the same words.
            AutomationProperties.SetName(button, text);
        }
    }

    private static T? Find<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed && string.Equals(typed.Name, name, StringComparison.Ordinal))
            {
                return typed;
            }

            if (Find<T>(child, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
