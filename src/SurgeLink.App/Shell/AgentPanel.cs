using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.Agent;
using SurgeLink.App.Localization;
using SurgeLink.App.Services;
using SurgeLink.App.Sessions;
using SurgeLink.App.Styling;
using SurgeLink.Core.Sessions;
using Windows.ApplicationModel.DataTransfer;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

internal sealed record AgentPanelProps(Action Close);

/// <summary>One non-modal home for Agent access, setup, takeover and activity.</summary>
internal sealed class AgentPanel : Component<AgentPanelProps>
{
    public override Element Render()
    {
        var services = UseContext(AppServices.ReactorContext) ?? throw new InvalidOperationException("Missing services.");
        var workspace = UseExternalStore(services.Workspace.Subscribe, () => services.Workspace.Snapshot);
        _ = UseExternalStore(services.Agent.Subscribe, () => services.Agent.Revision);
        var agent = services.Agent;
        var busy = UseRef(false);
        var (working, setWorking) = UseState(false);
        var (copied, setCopied) = UseState<string?>(null);

        async void ChangeAccess(WorkspaceTab tab, AgentAccessMode mode)
        {
            if (busy.Current) return;
            busy.Current = true;
            setWorking(true);
            try { await agent.SetAccessAsync(tab, mode); }
            finally { busy.Current = false; setWorking(false); }
        }

        async void StopAll()
        {
            try { await agent.StopAllAsync(); }
            catch (Exception ex) { CrashLog.Write("AgentStop", ex); }
        }

        void Copy(string text)
        {
            try
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
                setCopied(Str.T("shell.agent.copied", "Copied"));
            }
            catch { setCopied(Str.T("shell.agent.copyFailed", "Could not access the clipboard.")); }
        }

        Element SessionCard(WorkspaceTab tab)
        {
            var mode = agent.GetMode(tab.Id);
            var connected = tab.State == ConnectionState.Connected && !tab.IsClosing;
            var status = mode switch
            {
                AgentAccessMode.Control => Str.T("shell.agent.controlling", "Terminal control enabled"),
                AgentAccessMode.Observe => Str.T("shell.agent.observing", "Read access enabled"),
                _ => Str.T("shell.agent.off", "Not shared"),
            };
            return Card(VStack(AppTheme.SpacingS,
                BodyStrong(tab.Title).Private(tab.Title).TextTrimming(TextTrimming.CharacterEllipsis),
                Caption(status).Foreground(mode == AgentAccessMode.Control ? Theme.Accent : Theme.SecondaryText),
                Grid(columns: [GridSize.Star(), GridSize.Star()], rows: [GridSize.Auto, GridSize.Auto],
                    Button(Str.T("shell.agent.read", "Allow reading"), () => ChangeAccess(tab, AgentAccessMode.Observe))
                        .IsEnabled(!working && connected && mode != AgentAccessMode.Observe)
                        .AutomationName(Str.T("shell.agent.read", "Allow reading")).AutomationId("AgentRead-" + tab.Id)
                        .HAlign(HorizontalAlignment.Stretch).Margin(0, 0, AppTheme.SpacingXs, 0).Grid(row: 0, column: 0),
                    Button(Str.T("shell.agent.control", "Control terminal"), () => ChangeAccess(tab, AgentAccessMode.Control))
                        .IsEnabled(!working && connected && tab.Terminal is { IsClosed: false } && mode != AgentAccessMode.Control)
                        .AutomationName(Str.T("shell.agent.control", "Control terminal")).AutomationId("AgentControl-" + tab.Id)
                        .HAlign(HorizontalAlignment.Stretch).Grid(row: 0, column: 1),
                    Button(Str.T("shell.agent.stop", "Stop"), () => ChangeAccess(tab, AgentAccessMode.None))
                        .IsEnabled(!working && mode != AgentAccessMode.None)
                        .AutomationName(Str.T("shell.agent.stop", "Stop")).AutomationId("AgentStop-" + tab.Id)
                        .Margin(0, AppTheme.SpacingXs, 0, 0).Grid(row: 1, columnSpan: 2))))
                .WithKey(tab.Id.ToString());
        }

        var sessions = workspace.Tabs.Count == 0
            ? new[] { Caption(Str.T("shell.agent.empty", "Open a session to allow Agent access.")).Foreground(Theme.SecondaryText) }
            : workspace.Tabs.Select(SessionCard).ToArray();
        var activities = agent.Activity.Take(8).Select(entry => VStack(AppTheme.SpacingXs,
                Caption(Str.F("shell.agent.activityLine", "{0} · {1}", entry.Time.ToLocalTime().ToString("HH:mm:ss"), OperationLabel(entry.Operation))),
                Caption(entry.Session).Private(entry.Session).Foreground(Theme.SecondaryText).TextTrimming(TextTrimming.CharacterEllipsis),
                Caption(entry.Outcome == "ok" && entry.Operation is "terminal.text" or "terminal.key"
                    ? Str.T("shell.agent.result.sent", "Input sent") : OutcomeLabel(entry.Outcome))
                    .Foreground(Theme.SecondaryText)).WithKey(entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();

        return Border(Grid(
                columns: [GridSize.Star()], rows: [GridSize.Auto, GridSize.Star()],
                Grid(columns: [GridSize.Star(), GridSize.Auto], rows: [GridSize.Auto],
                    HStack(AppTheme.SpacingS, Icon(FontIcon("\uE99A", fontSize: 18)), Subtitle(Str.T("shell.agent.title", "Agent")))
                        .Grid(column: 0).VAlign(VerticalAlignment.Center),
                    Button(Icon(FontIcon("\uE8BB", fontSize: 12)), Props.Close).SubtleButton()
                        .AutomationName(Str.T("shell.agent.close", "Close Agent panel")).AutomationId("AgentPanelClose").Grid(column: 1))
                    .Margin(AppTheme.SpacingL).Grid(row: 0),
                ScrollViewer(VStack(AppTheme.SpacingL,
                    Body(Str.T("shell.agent.description", "Let your Agent work in the visible terminal. Execution policy is managed by the Agent application."))
                        .TextWrapping(TextWrapping.Wrap),
                    Caption(Str.T("shell.agent.scope", "Access applies to local programs running as your Windows user. Typing in a controlled terminal takes over and revokes access."))
                        .TextWrapping(TextWrapping.Wrap).Foreground(Theme.SecondaryText),
                    agent.Error is null ? Empty() : InfoBar(Str.T("shell.agent.errorTitle", "Agent access unavailable"),
                        Str.T("shell.agent.errorBody", "Could not change Agent access. Check the application log and try again.")),
                    VStack(AppTheme.SpacingS,
                        BodyStrong(Str.T("shell.agent.connect", "Connect your Agent")),
                        Caption(Str.F("shell.agent.instance", "Desktop instance: {0}", Environment.ProcessId)).Foreground(Theme.SecondaryText),
                        HStack(AppTheme.SpacingS,
                            Button(Str.T("shell.agent.copyMcp", "Copy MCP config"), () => Copy(agent.McpConfiguration))
                                .AutomationName(Str.T("shell.agent.copyMcp", "Copy MCP config")).AutomationId("AgentCopyMcp"),
                            Button(Str.T("shell.agent.copyCli", "Copy CLI command"), () => Copy("& '" + agent.CliPath.Replace("'", "''", StringComparison.Ordinal)
                                + "' sessions list --instance " + Environment.ProcessId + " --json"))
                                .AutomationName(Str.T("shell.agent.copyCli", "Copy CLI command")).AutomationId("AgentCopyCli")),
                        copied is null ? Empty() : Caption(copied).Foreground(Theme.SecondaryText)),
                    VStack(AppTheme.SpacingS,
                        BodyStrong(Str.T("shell.agent.sessions", "Session access")),
                        VStack(AppTheme.SpacingS, sessions)),
                    Button(Str.T("shell.agent.stopAll", "Stop all Agent access"), StopAll)
                        .IsEnabled(agent.HasAccess)
                        .AutomationName(Str.T("shell.agent.stopAll", "Stop all Agent access")).AutomationId("AgentStopAll"),
                    VStack(AppTheme.SpacingS,
                        BodyStrong(Str.T("shell.agent.activity", "Recent activity")),
                        activities.Length == 0 ? Caption(Str.T("shell.agent.noActivity", "Agent activity will appear here.")).Foreground(Theme.SecondaryText)
                            : VStack(AppTheme.SpacingM, activities)))
                    .Margin(AppTheme.SpacingL, 0, AppTheme.SpacingL, AppTheme.SpacingL))
                    .Set(viewer => viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled).Grid(row: 1)))
            .Background(AppTheme.PanelBackground).BorderBrush(Theme.CardStroke).BorderThickness(1)
            .CornerRadius(AppTheme.CardRadius).AutomationId("AgentPanel");
    }

    private static string OperationLabel(string operation) => operation switch
    {
        "terminal.text" => Str.T("shell.agent.action.text", "Send terminal text"),
        "terminal.key" => Str.T("shell.agent.action.key", "Send terminal key"),
        "terminal.screen" or "terminal.output" => Str.T("shell.agent.action.read", "Read terminal"),
        "sessions.activate" => Str.T("shell.agent.action.select", "Select session"),
        "access" => Str.T("shell.agent.action.access", "Access changed"),
        _ => Str.T("shell.agent.action.request", "Agent request"),
    };

    private static string OutcomeLabel(string outcome) => outcome switch
    {
        "ok" => Str.T("shell.agent.result.ok", "Completed"),
        "control_enabled" => Str.T("shell.agent.controlling", "Terminal control enabled"),
        "read_enabled" => Str.T("shell.agent.observing", "Read access enabled"),
        "human_input" => Str.T("shell.agent.result.takeover", "You took over; Agent access stopped"),
        "revoked" or "connection_closed" => Str.T("shell.agent.result.stopped", "Agent access stopped"),
        "input_outcome_unknown" => Str.T("shell.agent.result.uncertain", "Input outcome unknown; inspect the terminal"),
        _ => Str.T("shell.agent.result.failed", "Request could not complete"),
    };
}
