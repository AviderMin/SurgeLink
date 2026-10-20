using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Reactor.Layout;
using SurgeLink.App.Services;
using SurgeLink.App.Styling;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Storage;
using SurgeLink.Terminal;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

/// <summary>
/// Settings surface, shown in the shell content area in place of the workspace.
/// Every value is read from and written to <see cref="IAppSettingsStore"/>, so
/// the terminal, the monitor and the file browser pick changes up live through
/// the store subscription they already have.
/// </summary>
internal sealed class SettingsPage : Component
{
    private static readonly int[] ScrollbackOptions = { 1000, 5000, 10000, 25000, 50000, 100000 };

    private static readonly int[] IntervalOptions = { 1, 2, 5, 10, 30 };

    public override Element Render()
    {
        var services = AppServices.EnsureInitialized();
        var store = services.Settings;
        var settings = UseExternalStore(store.Subscribe, () => store.Current);

        void Apply(Func<AppSettings, AppSettings> mutate) => store.Update(mutate);

        var fontChoices = new List<string> { "(auto detect)" };
        fontChoices.AddRange(AppTheme.MonospaceCandidates);

        return ScrollView(
            VStack(
                AppTheme.SpacingXl,
                VStack(
                    AppTheme.SpacingXs,
                    Heading("Settings").HeadingLevel(AutomationHeadingLevel.Level1),
                    Caption("Changes apply immediately and are saved to disk.")
                        .Foreground(Theme.SecondaryText))
                    .Margin(0, 0, 0, AppTheme.SpacingS),

                Section(
                    "Appearance",
                    SettingsCard(
                        "Theme",
                        "Light, dark, or follow the Windows setting. High Contrast keeps working in every mode.",
                        RadioButtons(["System", "Light", "Dark"], Optional<int>.Of((int)settings.Theme), index =>
                                Apply(current => current with { Theme = (ThemeMode)index }))
                            .AutomationName("Theme")
                            .AutomationId("SettingsTheme"))),

                Section(
                    "Terminal",
                    SettingsCard(
                        "Font family",
                        "Monospace face used by every terminal tab.",
                        ComboBox(fontChoices.ToArray(),
                                Optional<int>.Of(FontIndex(fontChoices, settings.TerminalFontFamily)), index =>
                                    Apply(current => current with
                                    {
                                        TerminalFontFamily = index <= 0 ? string.Empty : fontChoices[index],
                                    }))
                            .AutomationName("Terminal font family")
                            .AutomationId("SettingsFontFamily")
                            .MinWidth(220)),
                    SettingsCard(
                        "Font size",
                        "Terminal text size in points. Ctrl+wheel inside a terminal changes this too.",
                        HStack(
                                AppTheme.SpacingM,
                                Slider(Optional<double>.Of(settings.TerminalFontSize), 9, 28, value =>
                                        Apply(current => current with { TerminalFontSize = Math.Round(value) }))
                                    .Set(slider =>
                                    {
                                        slider.StepFrequency = 1;
                                        slider.TickFrequency = 1;
                                    })
                                    .AutomationName("Terminal font size")
                                    .AutomationId("SettingsFontSize")
                                    .Width(220),
                                Caption($"{settings.TerminalFontSize:0} pt").Width(48))
                            .VAlign(VerticalAlignment.Center)),
                    SettingsCard(
                        "Colour palette",
                        "ANSI scheme used by the terminal renderer.",
                        ComboBox(TerminalThemes.All.Select(theme => theme.Name).ToArray(),
                                Optional<int>.Of(PaletteIndex(settings.TerminalPalette)),
                                index => Apply(current => current with
                                {
                                    TerminalPalette = TerminalThemes.All.Count > index && index >= 0
                                        ? TerminalThemes.All[index].Name
                                        : current.TerminalPalette,
                                }))
                            .AutomationName("Terminal colour palette")
                            .AutomationId("SettingsPalette")
                            .MinWidth(220)),
                    SettingsCard(
                        "Scrollback",
                        "Lines of history kept in memory for each terminal tab.",
                        ComboBox(ScrollbackOptions.Select(lines => lines.ToString("N0") + " lines").ToArray(),
                                Optional<int>.Of(ClosestIndex(ScrollbackOptions, settings.TerminalScrollbackLines)),
                                index => Apply(current => current with
                                {
                                    TerminalScrollbackLines = ScrollbackOptions[index],
                                }))
                            .AutomationName("Terminal scrollback")
                            .AutomationId("SettingsScrollback")
                            .MinWidth(220))),

                Section(
                    "Monitoring",
                    SettingsCard(
                        "Sample interval",
                        "How often the remote performance dashboard polls the server.",
                        ComboBox(IntervalOptions.Select(seconds => seconds + " seconds").ToArray(),
                                Optional<int>.Of(ClosestIndex(IntervalOptions, settings.MonitorIntervalSeconds)),
                                index => Apply(current => current with
                                {
                                    MonitorIntervalSeconds = IntervalOptions[index],
                                }))
                            .AutomationName("Monitor sample interval")
                            .AutomationId("SettingsMonitorInterval")
                            .MinWidth(220))),

                Section(
                    "Files",
                    SettingsCard(
                        "Show hidden files",
                        "List dot files in the SFTP browser.",
                        ToggleSwitch(Optional<bool>.Of(settings.ShowHiddenFiles),
                                value => Apply(current => current with { ShowHiddenFiles = value }),
                                onContent: "On",
                                offContent: "Off")
                            .AutomationName("Show hidden files")
                            .AutomationId("SettingsHiddenFiles"))),

                Section(
                    "Sessions",
                    SettingsCard(
                        "Confirm closing connected tabs",
                        "Ask before a tab whose session is still connected is closed.",
                        ToggleSwitch(Optional<bool>.Of(settings.ConfirmCloseConnectedTab),
                                value => Apply(current => current with { ConfirmCloseConnectedTab = value }),
                                onContent: "On",
                                offContent: "Off")
                            .AutomationName("Confirm closing connected tabs")
                            .AutomationId("SettingsConfirmClose"))),

                Section(
                    "Diagnostics",
                    SettingsCard(
                        "Prompt previews",
                        "Render each protocol prompt without a server. Useful for checking the dialog layer and accessibility.",
                        WrapButtons(
                            Button("Host key prompt", () => _ = PreviewHostKeyAsync(isChanged: false))
                                .AutomationName("Preview host key prompt"),
                            Button("Changed key warning", () => _ = PreviewHostKeyAsync(isChanged: true))
                                .AutomationName("Preview changed host key"),
                            Button("Password prompt", () => _ = PreviewSecretAsync())
                                .AutomationName("Preview password prompt"),
                            Button("Confirmation", () => _ = PreviewConfirmationAsync())
                                .AutomationName("Preview confirmation"),
                            Button("Error dialog", () => _ = PreviewErrorAsync())
                                .AutomationName("Preview error dialog"))),
                    SettingsCard(
                        "Storage",
                        "Profiles, credentials and logs live in one folder.",
                        VStack(
                                AppTheme.SpacingXs,
                                TextBlock(AppPaths.Root).FontFamily(AppTheme.Monospace).FontSize(13)
                                    .IsTextSelectionEnabled(true),
                                Caption($"SurgeLink {typeof(SettingsPage).Assembly.GetName().Version}")
                                    .Foreground(Theme.SecondaryText))
                            .HAlign(HorizontalAlignment.Left)),
                    SettingsCard(
                        "Reset",
                        "Restore every preference to its default value.",
                        Button("Reset settings", () => Apply(_ => AppSettings.Default))
                            .AutomationName("Reset settings"))))
                .MaxWidth(880)
                .HAlign(HorizontalAlignment.Left)
                .Padding(AppTheme.SpacingXl));
    }

    private static Element Section(string title, params Element[] rows) =>
        VStack(
            AppTheme.SpacingS,
            SubHeading(title).HeadingLevel(AutomationHeadingLevel.Level2),
            VStack(AppTheme.SpacingS, rows));

    /// <summary>Label + description on the left, the control on the right.</summary>
    private static Element SettingsCard(string title, string description, Element control) =>
        Card(
            Grid(
                columns: [GridSize.Star(), GridSize.Auto],
                rows: [GridSize.Auto, GridSize.Auto],
                BodyStrong(title).Grid(row: 0, column: 0),
                Caption(description)
                    .Foreground(Theme.SecondaryText)
                    .TextWrapping(TextWrapping.Wrap)
                    .Grid(row: 1, column: 0)
                    .Margin(0, AppTheme.SpacingXs, AppTheme.SpacingM, 0),
                control.Grid(row: 0, column: 1, rowSpan: 2).VAlign(VerticalAlignment.Center)) with
            {
                ColumnSpacing = AppTheme.SpacingL,
            });

    private static Element WrapButtons(params Element[] buttons) =>
        FlexRow(buttons) with
        {
            ColumnGap = AppTheme.SpacingS,
            RowGap = AppTheme.SpacingS,
            Wrap = FlexWrap.Wrap,
        };

    private static int FontIndex(IReadOnlyList<string> choices, string? family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return 0;
        }

        for (var index = 1; index < choices.Count; index++)
        {
            if (string.Equals(choices[index], family, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }

    private static int PaletteIndex(string? name)
    {
        for (var index = 0; index < TerminalThemes.All.Count; index++)
        {
            if (string.Equals(TerminalThemes.All[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }

    private static int ClosestIndex(IReadOnlyList<int> values, int value)
    {
        var best = 0;
        for (var index = 1; index < values.Count; index++)
        {
            if (Math.Abs(values[index] - value) < Math.Abs(values[best] - value))
            {
                best = index;
            }
        }

        return best;
    }

    private static async Task PreviewHostKeyAsync(bool isChanged)
    {
        var interaction = AppServices.EnsureInitialized().Interaction;
        var decision = await interaction.RequestHostKeyTrustAsync(
            new HostKeyChallenge(
                "demo-host.example.com",
                22,
                "ssh-ed25519",
                "SHA256:47DEQpj8HBSa+TImW+5JCeuQeRkm5NMpJWZG3hSuFU",
                isChanged),
            CancellationToken.None);

        await interaction.ShowInfoAsync(
            "Host key decision",
            decision.Trusted
                ? decision.Remember ? "Trusted and remembered." : "Trusted for this session only."
                : "Rejected. The connection would have been aborted.");
    }

    private static async Task PreviewSecretAsync()
    {
        var interaction = AppServices.EnsureInitialized().Interaction;
        var secret = await interaction.RequestSecretAsync(
            new SecretRequest("Password for admin@demo-host.example.com", "This login is not saved. Enter the password for this session."),
            CancellationToken.None);

        await interaction.ShowInfoAsync(
            "Password prompt",
            secret is null ? "Dismissed without an answer." : $"Received {secret.Length} character(s).");
    }

    private static async Task PreviewConfirmationAsync()
    {
        var interaction = AppServices.EnsureInitialized().Interaction;
        var confirmed = await interaction.RequestConfirmationAsync(
            new ConfirmationRequest("Close session", "This session is still connected. Close it?", "Close", "Keep open"),
            CancellationToken.None);

        await interaction.ShowInfoAsync("Confirmation", confirmed ? "Confirmed." : "Cancelled.");
    }

    private static Task PreviewErrorAsync() =>
        AppServices.EnsureInitialized().Interaction.ShowErrorAsync(
            "Connection failed",
            "This is a preview of the error dialog. Protocol failures arrive here with a plain message.");
}
