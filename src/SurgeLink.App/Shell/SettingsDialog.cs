using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Localization;
using SurgeLink.App.Services;
using SurgeLink.App.Styling;
using SurgeLink.Terminal;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

/// <summary>Props for the modal settings dialog.</summary>
internal sealed record SettingsDialogProps(Action OnClosed, Action<Exception> OnSaveFailed);

/// <summary>
/// Settings in a native dialog shared with About. Changes are
/// staged in a local draft and only written when the user chooses Save; Cancel
/// and the close affordance discard the draft.
/// </summary>
internal sealed class SettingsDialog : Component<SettingsDialogProps>
{
    private static readonly int[] ScrollbackOptions = { 1000, 5000, 10000, 25000, 50000, 100000 };
    private static readonly int[] IntervalOptions = { 1, 2, 5, 10, 30 };

    public override Element Render()
    {
        var services = UseContext(AppServices.ReactorContext)
            ?? throw new InvalidOperationException("The component must be inside the shell service context.");
        var store = services.Settings;
        var settings = UseExternalStore(store.Subscribe, () => store.Current);
        var (draft, setDraft) = UseState(store.Current);
        var (saveError, setSaveError) = UseState<string?>(null);
        var closed = UseRef(false);
        var dialogControl = UseRef<ContentDialog?>(null);
        // PrimaryButtonClick runs before OnClosed. Keeping the native dialog open
        // on a failed write preserves both the user's draft and their retry path.
        UseEffect(() => () =>
        {
            if (dialogControl.Current is { } control) control.PrimaryButtonClick -= CancelFailedSave;
        }, Array.Empty<object>());
        void CancelFailedSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (!SettingsDialogSave.TryApply(store, draft, error =>
                {
                    Props.OnSaveFailed(error);
                    setSaveError(error.Message);
                }))
            {
                args.Cancel = true;
            }
        }

        var fontChoices = new List<string> { Str.T("shell.settings.fontFamily.auto", "(auto detect)") };
        fontChoices.AddRange(AppTheme.MonospaceCandidates);

        var content = VStack(
            AppTheme.SpacingXl,
            Section(
                Str.T("shell.settings.general", "General"),
                SettingRow(
                    Str.T("shell.settings.privacy", "Privacy mode"),
                    Str.T("shell.settings.privacy.description", "Hide server IP addresses and domains with ***. Hover to reveal. Terminal output and remote file contents are unchanged."),
                    ToggleSwitch(Optional<bool>.Of(draft.PrivacyMode),
                            value => setDraft(draft with { PrivacyMode = value }),
                            onContent: Str.T("shell.settings.on", "On"),
                            offContent: Str.T("shell.settings.off", "Off"))
                        .AutomationName(Str.T("shell.settings.privacy", "Privacy mode"))
                        .AutomationId("SettingsPrivacyMode")),
                SettingRow(
                    Str.T("shell.settings.language", "Interface language"),
                    Str.T("shell.settings.language.description", "Language used by menus, dialogs and this page."),
                    ComboBox(
                            LanguageNames(),
                            Optional<int>.Of(IndexOfLanguage(draft.Language)),
                            index =>
                            {
                                if (index >= 0 && index < LanguageOrder.Length)
                                {
                                    setDraft(draft with { Language = LanguageOrder[index] });
                                }
                            })
                        .AutomationName(Str.T("shell.settings.language", "Interface language"))
                        .AutomationId("SettingsLanguage")
                        .MinWidth(200))),
            Section(
                Str.T("shell.settings.appearance", "Appearance"),
                SettingRow(
                    Str.T("shell.settings.theme", "Theme"),
                    Str.T("shell.settings.theme.description", "Light, dark, or follow the Windows setting."),
                    ComboBox(
                            [Str.T("shell.settings.theme.system", "System"), Str.T("shell.settings.theme.light", "Light"), Str.T("shell.settings.theme.dark", "Dark")],
                            Optional<int>.Of((int)draft.Theme),
                            index => setDraft(draft with { Theme = (ThemeMode)index }))
                        .AutomationName(Str.T("shell.settings.theme", "Theme"))
                        .AutomationId("SettingsTheme")
                        .MinWidth(200))),
            Section(
                Str.T("shell.settings.terminal", "Terminal"),
                SettingRow(
                    Str.T("shell.settings.fontFamily", "Font family"),
                    Str.T("shell.settings.fontFamily.description", "Monospace face used by every terminal tab."),
                    ComboBox(
                            fontChoices.ToArray(),
                            Optional<int>.Of(FontIndex(fontChoices, draft.TerminalFontFamily)),
                            index => setDraft(draft with
                            {
                                TerminalFontFamily = index <= 0 ? string.Empty : fontChoices[index],
                            }))
                        .AutomationName(Str.T("shell.settings.fontFamily", "Font family"))
                        .AutomationId("SettingsFontFamily")
                        .MinWidth(200)),
                SettingRow(
                    Str.T("shell.settings.fontSize", "Font size"),
                    Str.T("shell.settings.fontSize.description", "Terminal text size in points."),
                    HStack(
                            AppTheme.SpacingM,
                            Slider(Optional<double>.Of(draft.TerminalFontSize), 9, 28, value =>
                                    setDraft(draft with { TerminalFontSize = Math.Round(value) }))
                                .Set(slider =>
                                {
                                    slider.StepFrequency = 1;
                                    slider.TickFrequency = 1;
                                })
                                .AutomationName(Str.T("shell.settings.fontSize", "Font size"))
                                .Width(180),
                            Caption(Str.F("shell.settings.fontSize.value", "{0} pt", draft.TerminalFontSize.ToString("0")))
                                .Width(48))
                        .VAlign(VerticalAlignment.Center)),
                SettingRow(
                    Str.T("shell.settings.palette", "Colour palette"),
                    Str.T("shell.settings.palette.description", "ANSI scheme used by the terminal renderer."),
                    ComboBox(
                            TerminalThemes.All.Select(theme => theme.Name).ToArray(),
                            Optional<int>.Of(PaletteIndex(draft.TerminalPalette)),
                            index => setDraft(draft with
                            {
                                TerminalPalette = TerminalThemes.All.Count > index && index >= 0
                                    ? TerminalThemes.All[index].Name
                                    : draft.TerminalPalette,
                            }))
                        .AutomationName(Str.T("shell.settings.palette", "Colour palette"))
                        .MinWidth(200)),
                SettingRow(
                    Str.T("shell.settings.scrollback", "Scrollback"),
                    Str.T("shell.settings.scrollback.description", "Lines of history kept in memory for each terminal tab."),
                    ComboBox(
                            ScrollbackOptions.Select(lines => Str.F(
                                "shell.settings.scrollback.value", "{0} lines", lines.ToString("N0"))).ToArray(),
                            Optional<int>.Of(ClosestIndex(ScrollbackOptions, draft.TerminalScrollbackLines)),
                            index =>
                            {
                                if (index >= 0 && index < ScrollbackOptions.Length)
                                {
                                    setDraft(draft with { TerminalScrollbackLines = ScrollbackOptions[index] });
                                }
                            })
                        .AutomationName(Str.T("shell.settings.scrollback", "Scrollback"))
                        .MinWidth(200))),
            Section(
                Str.T("shell.settings.monitoring", "Monitoring"),
                SettingRow(
                    Str.T("shell.settings.monitorInterval", "Sample interval"),
                    Str.T("shell.settings.monitorInterval.description", "How often the remote performance dashboard polls the server."),
                    ComboBox(
                            IntervalOptions.Select(seconds => Str.F(
                                "shell.settings.monitorInterval.value", "{0} seconds", seconds)).ToArray(),
                            Optional<int>.Of(ClosestIndex(IntervalOptions, draft.MonitorIntervalSeconds)),
                            index =>
                            {
                                if (index >= 0 && index < IntervalOptions.Length)
                                {
                                    setDraft(draft with { MonitorIntervalSeconds = IntervalOptions[index] });
                                }
                            })
                        .AutomationName(Str.T("shell.settings.monitorInterval", "Sample interval"))
                        .MinWidth(200))),
            Section(
                Str.T("shell.settings.files", "Files"),
                SettingRow(
                    Str.T("shell.settings.hiddenFiles", "Show hidden files"),
                    Str.T("shell.settings.hiddenFiles.description", "List dot files in the SFTP browser."),
                    ToggleSwitch(
                            Optional<bool>.Of(draft.ShowHiddenFiles),
                            value => setDraft(draft with { ShowHiddenFiles = value }),
                            onContent: Str.T("shell.settings.on", "On"),
                            offContent: Str.T("shell.settings.off", "Off"))
                        .AutomationName(Str.T("shell.settings.hiddenFiles", "Show hidden files")))),
            Section(
                Str.T("shell.settings.sessions", "Sessions"),
                SettingRow(
                    Str.T("shell.settings.confirmClose", "Confirm closing connected tabs"),
                    Str.T("shell.settings.confirmClose.description", "Ask before a tab whose session is still connected is closed."),
                    ToggleSwitch(
                            Optional<bool>.Of(draft.ConfirmCloseConnectedTab),
                            value => setDraft(draft with { ConfirmCloseConnectedTab = value }),
                            onContent: Str.T("shell.settings.on", "On"),
                            offContent: Str.T("shell.settings.off", "Off"))
                        .AutomationName(Str.T("shell.settings.confirmClose", "Confirm closing connected tabs")))));

        return AppDialog.Render(
            Str.T("shell.settings.title", "Settings"),
            VStack(AppTheme.SpacingL,
                Caption(Str.T("shell.settings.subtitle", "Changes are applied when you save."))
                    .Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap),
                saveError is null ? Empty() : InfoBar(
                    Str.T("shell.error.saveFailed", "Could not save changes"),
                    Str.F("shell.settings.saveFailedRetry",
                        "Your changes are still here. Check folder access and available disk space, then try saving again. {0}",
                        saveError)) with
                {
                    Severity = InfoBarSeverity.Error,
                    IsClosable = false,
                },
                content),
            "Settings",
            result =>
            {
                if (closed.Current) return;
                closed.Current = true;
                if (dialogControl.Current is { } control)
                {
                    control.PrimaryButtonClick -= CancelFailedSave;
                    dialogControl.Current = null;
                }
                Props.OnClosed();
            },
            Str.T("common.save", "Save"),
            Str.T("common.cancel", "Cancel"),
            theme: ThemeController.ToElementTheme(settings.Theme),
            isOpen: !closed.Current,
            contentWidth: AppDialog.ContentWidth,
            onOpened: control =>
            {
                // AppDialog invokes OnOpened on every render and again when the
                // native dialog opens; attaching each time would stack duplicate
                // handlers whose stale drafts could later overwrite saved values.
                if (ReferenceEquals(dialogControl.Current, control))
                {
                    return;
                }

                if (dialogControl.Current is { } previous)
                {
                    previous.PrimaryButtonClick -= CancelFailedSave;
                }

                dialogControl.Current = control;
                control.PrimaryButtonClick += CancelFailedSave;
            });
    }

    private static Element Section(string title, params Element[] rows) =>
        VStack(
            AppTheme.SpacingS,
            SubHeading(title).HeadingLevel(Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2),
            VStack(AppTheme.SpacingM, rows));

    private static Element SettingRow(string title, string description, Element control) =>
        Card(VStack(
            AppTheme.SpacingM,
            VStack(AppTheme.SpacingXs,
                BodyStrong(title).TextWrapping(TextWrapping.Wrap),
                Caption(description).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap)),
            control.HAlign(HorizontalAlignment.Stretch)));

    private static readonly AppLanguage[] LanguageOrder =
    {
        AppLanguage.System,
        AppLanguage.ChineseSimplified,
        AppLanguage.English,
    };

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
}
