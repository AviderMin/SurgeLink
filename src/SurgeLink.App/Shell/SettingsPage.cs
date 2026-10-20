using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Reactor.Layout;
using SurgeLink.App.Localization;
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

        var fontChoices = new List<string> { Str.T("shell.settings.fontFamily.auto", "(auto detect)") };
        fontChoices.AddRange(AppTheme.MonospaceCandidates);

        var onText = Str.T("shell.settings.on", "On");
        var offText = Str.T("shell.settings.off", "Off");

        return ScrollView(
            VStack(
                AppTheme.SpacingXl,
                VStack(
                    AppTheme.SpacingXs,
                    Heading(Str.T("shell.settings.title", "Settings")).HeadingLevel(AutomationHeadingLevel.Level1),
                    Caption(Str.T("shell.settings.subtitle", "Changes apply immediately and are saved to disk."))
                        .Foreground(Theme.SecondaryText))
                    .Margin(0, 0, 0, AppTheme.SpacingS),

                Section(
                    Str.T("shell.settings.general", "General"),
                    SettingsCard(
                        Str.T("shell.settings.language", "Interface language"),
                        Str.T("shell.settings.language.description",
                            "Language used by the menus, dialogs and this page."),
                        ComboBox(LanguageNames(), Optional<int>.Of(IndexOfLanguage(settings.Language)),
                                index => Apply(current => current with { Language = LanguageOrder[index] }))
                            .AutomationName(Str.T("shell.settings.language", "Interface language"))
                            .AutomationId("SettingsLanguage")
                            .MinWidth(220))),

                Section(
                    Str.T("shell.settings.appearance", "Appearance"),
                    SettingsCard(
                        Str.T("shell.settings.theme", "Theme"),
                        Str.T("shell.settings.theme.description",
                            "Light, dark, or follow the Windows setting. High Contrast keeps working in every mode."),
                        RadioButtons(
                                [
                                    Str.T("shell.settings.theme.system", "System"),
                                    Str.T("shell.settings.theme.light", "Light"),
                                    Str.T("shell.settings.theme.dark", "Dark"),
                                ],
                                Optional<int>.Of((int)settings.Theme),
                                index => Apply(current => current with { Theme = (ThemeMode)index }))
                            .AutomationName(Str.T("shell.settings.theme", "Theme"))
                            .AutomationId("SettingsTheme"))),

                Section(
                    Str.T("shell.settings.terminal", "Terminal"),
                    SettingsCard(
                        Str.T("shell.settings.fontFamily", "Font family"),
                        Str.T("shell.settings.fontFamily.description", "Monospace face used by every terminal tab."),
                        ComboBox(fontChoices.ToArray(),
                                Optional<int>.Of(FontIndex(fontChoices, settings.TerminalFontFamily)), index =>
                                    Apply(current => current with
                                    {
                                        TerminalFontFamily = index <= 0 ? string.Empty : fontChoices[index],
                                    }))
                            .AutomationName(Str.T("shell.settings.fontFamily", "Font family"))
                            .AutomationId("SettingsFontFamily")
                            .MinWidth(220)),
                    SettingsCard(
                        Str.T("shell.settings.fontSize", "Font size"),
                        Str.T("shell.settings.fontSize.description",
                            "Terminal text size in points. Ctrl+wheel inside a terminal changes this too."),
                        HStack(
                                AppTheme.SpacingM,
                                Slider(Optional<double>.Of(settings.TerminalFontSize), 9, 28, value =>
                                        Apply(current => current with { TerminalFontSize = Math.Round(value) }))
                                    .Set(slider =>
                                    {
                                        slider.StepFrequency = 1;
                                        slider.TickFrequency = 1;
                                    })
                                    .AutomationName(Str.T("shell.settings.fontSize", "Font size"))
                                    .AutomationId("SettingsFontSize")
                                    .Width(220),
                                Caption(Str.F("shell.settings.fontSize.value", "{0} pt", settings.TerminalFontSize.ToString("0")))
                                    .Width(56))
                            .VAlign(VerticalAlignment.Center)),
                    SettingsCard(
                        Str.T("shell.settings.palette", "Colour palette"),
                        Str.T("shell.settings.palette.description", "ANSI scheme used by the terminal renderer."),
                        ComboBox(TerminalThemes.All.Select(theme => theme.Name).ToArray(),
                                Optional<int>.Of(PaletteIndex(settings.TerminalPalette)),
                                index => Apply(current => current with
                                {
                                    TerminalPalette = TerminalThemes.All.Count > index && index >= 0
                                        ? TerminalThemes.All[index].Name
                                        : current.TerminalPalette,
                                }))
                            .AutomationName(Str.T("shell.settings.palette", "Colour palette"))
                            .AutomationId("SettingsPalette")
                            .MinWidth(220)),
                    SettingsCard(
                        Str.T("shell.settings.scrollback", "Scrollback"),
                        Str.T("shell.settings.scrollback.description",
                            "Lines of history kept in memory for each terminal tab."),
                        ComboBox(
                                ScrollbackOptions.Select(lines => Str.F(
                                    "shell.settings.scrollback.value", "{0} lines", lines.ToString("N0"))).ToArray(),
                                Optional<int>.Of(ClosestIndex(ScrollbackOptions, settings.TerminalScrollbackLines)),
                                index => Apply(current => current with
                                {
                                    TerminalScrollbackLines = ScrollbackOptions[index],
                                }))
                            .AutomationName(Str.T("shell.settings.scrollback", "Scrollback"))
                            .AutomationId("SettingsScrollback")
                            .MinWidth(220))),

                Section(
                    Str.T("shell.settings.monitoring", "Monitoring"),
                    SettingsCard(
                        Str.T("shell.settings.monitorInterval", "Sample interval"),
                        Str.T("shell.settings.monitorInterval.description",
                            "How often the remote performance dashboard polls the server."),
                        ComboBox(
                                IntervalOptions.Select(seconds => Str.F(
                                    "shell.settings.monitorInterval.value", "{0} seconds", seconds)).ToArray(),
                                Optional<int>.Of(ClosestIndex(IntervalOptions, settings.MonitorIntervalSeconds)),
                                index => Apply(current => current with
                                {
                                    MonitorIntervalSeconds = IntervalOptions[index],
                                }))
                            .AutomationName(Str.T("shell.settings.monitorInterval", "Sample interval"))
                            .AutomationId("SettingsMonitorInterval")
                            .MinWidth(220))),

                Section(
                    Str.T("shell.settings.files", "Files"),
                    SettingsCard(
                        Str.T("shell.settings.hiddenFiles", "Show hidden files"),
                        Str.T("shell.settings.hiddenFiles.description", "List dot files in the SFTP browser."),
                        ToggleSwitch(Optional<bool>.Of(settings.ShowHiddenFiles),
                                value => Apply(current => current with { ShowHiddenFiles = value }),
                                onContent: onText,
                                offContent: offText)
                            .AutomationName(Str.T("shell.settings.hiddenFiles", "Show hidden files"))
                            .AutomationId("SettingsHiddenFiles"))),

                Section(
                    Str.T("shell.settings.sessions", "Sessions"),
                    SettingsCard(
                        Str.T("shell.settings.confirmClose", "Confirm closing connected tabs"),
                        Str.T("shell.settings.confirmClose.description",
                            "Ask before a tab whose session is still connected is closed."),
                        ToggleSwitch(Optional<bool>.Of(settings.ConfirmCloseConnectedTab),
                                value => Apply(current => current with { ConfirmCloseConnectedTab = value }),
                                onContent: onText,
                                offContent: offText)
                            .AutomationName(Str.T("shell.settings.confirmClose", "Confirm closing connected tabs"))
                            .AutomationId("SettingsConfirmClose"))),

                Section(
                    Str.T("shell.settings.diagnostics", "Diagnostics"),
                    SettingsCard(
                        Str.T("shell.settings.previews", "Prompt previews"),
                        Str.T("shell.settings.previews.description",
                            "Render each protocol prompt without a server. Useful for checking the dialog layer and accessibility."),
                        WrapButtons(
                            Button(Str.T("shell.settings.preview.hostKey", "Host key prompt"),
                                    () => _ = PreviewHostKeyAsync(isChanged: false))
                                .AutomationName(Str.T("shell.settings.preview.hostKey", "Host key prompt")),
                            Button(Str.T("shell.settings.preview.changedKey", "Changed key warning"),
                                    () => _ = PreviewHostKeyAsync(isChanged: true))
                                .AutomationName(Str.T("shell.settings.preview.changedKey", "Changed key warning")),
                            Button(Str.T("shell.settings.preview.secret", "Password prompt"),
                                    () => _ = PreviewSecretAsync())
                                .AutomationName(Str.T("shell.settings.preview.secret", "Password prompt")),
                            Button(Str.T("shell.settings.preview.confirmation", "Confirmation"),
                                    () => _ = PreviewConfirmationAsync())
                                .AutomationName(Str.T("shell.settings.preview.confirmation", "Confirmation")),
                            Button(Str.T("shell.settings.preview.error", "Error dialog"),
                                    () => _ = PreviewErrorAsync())
                                .AutomationName(Str.T("shell.settings.preview.error", "Error dialog"))))),
                    SettingsCard(
                        Str.T("shell.settings.storage", "Storage"),
                        Str.T("shell.settings.storage.description",
                            "Profiles, credentials and logs live in one folder."),
                        VStack(
                                AppTheme.SpacingXs,
                                TextBlock(AppPaths.Root).FontFamily(AppTheme.Monospace).FontSize(13)
                                    .IsTextSelectionEnabled(true),
                                Caption($"SurgeLink {typeof(SettingsPage).Assembly.GetName().Version}")
                                    .Foreground(Theme.SecondaryText))
                            .HAlign(HorizontalAlignment.Left)),
                    SettingsCard(
                        Str.T("shell.settings.reset", "Reset"),
                        Str.T("shell.settings.reset.description", "Restore every preference to its default value."),
                        Button(Str.T("shell.settings.reset.action", "Reset settings"), () => Apply(_ => AppSettings.Default))
                            .AutomationName(Str.T("shell.settings.reset.action", "Reset settings"))))
                .MaxWidth(880)
                .HAlign(HorizontalAlignment.Left)
                .Padding(AppTheme.SpacingXl));
    }

    /// <summary>
    /// Picker order. It is deliberately not the enum order, so the index must be
    /// mapped through this table instead of casting to <see cref="AppLanguage"/>.
    /// </summary>
    private static readonly AppLanguage[] LanguageOrder =
    {
        AppLanguage.System,
        AppLanguage.ChineseSimplified,
        AppLanguage.English,
    };

    /// <summary>Language choices shown in the picker; Chinese and English are self-named.</summary>
    private static string[] LanguageNames() =>
    [
        Str.T("shell.settings.language.system", "System default"),
        "简体中文",
        "English",
    ];

    private static int IndexOfLanguage(AppLanguage language)
    {
        for (var index = 0; index < LanguageOrder.Length; index++)
        {
            if (LanguageOrder[index] == language)
            {
                return index;
            }
        }

        return 0;
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
            Str.T("shell.preview.hostKey.title", "Host key decision"),
            decision.Trusted
                ? decision.Remember
                    ? Str.T("shell.preview.hostKey.trustedRemembered", "Trusted and remembered.")
                    : Str.T("shell.preview.hostKey.trustedOnce", "Trusted for this session only.")
                : Str.T("shell.preview.hostKey.rejected", "Rejected. The connection would have been aborted."));
    }

    private static async Task PreviewSecretAsync()
    {
        var interaction = AppServices.EnsureInitialized().Interaction;
        var secret = await interaction.RequestSecretAsync(
            new SecretRequest(
                Str.T("shell.preview.secret.title", "Password for admin@demo-host.example.com"),
                Str.T("shell.preview.secret.body",
                    "This login is not saved. Enter the password for this session.")),
            CancellationToken.None);

        await interaction.ShowInfoAsync(
            Str.T("shell.preview.secret.infoTitle", "Password prompt"),
            secret is null
                ? Str.T("shell.preview.secret.dismissed", "Dismissed without an answer.")
                : Str.F("shell.preview.secret.received", "Received {0} character(s).", secret.Length));
    }

    private static async Task PreviewConfirmationAsync()
    {
        var interaction = AppServices.EnsureInitialized().Interaction;
        var confirmed = await interaction.RequestConfirmationAsync(
            new ConfirmationRequest(
                Str.T("shell.preview.confirmation.requestTitle", "Close session"),
                Str.T("shell.preview.confirmation.requestBody", "This session is still connected. Close it?"),
                Str.T("common.close", "Close"),
                Str.T("shell.preview.confirmation.keepOpen", "Keep open")),
            CancellationToken.None);

        await interaction.ShowInfoAsync(
            Str.T("shell.preview.confirmation.title", "Confirmation"),
            confirmed
                ? Str.T("shell.preview.confirmation.confirmed", "Confirmed.")
                : Str.T("shell.preview.confirmation.cancelled", "Cancelled."));
    }

    private static Task PreviewErrorAsync() =>
        AppServices.EnsureInitialized().Interaction.ShowErrorAsync(
            Str.T("shell.preview.error.title", "Connection failed"),
            Str.T("shell.preview.error.body",
                "This is a preview of the error dialog. Protocol failures arrive here with a plain message."));
}
