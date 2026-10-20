using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Localization;
using SurgeLink.App.Services;
using SurgeLink.App.Sessions;
using SurgeLink.App.Styling;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Security;
using SurgeLink.Core.Sessions;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

/// <summary>Which surface fills the shell content area.</summary>
internal enum ShellSection
{
    Workspace,
    Settings,
}

/// <summary>One open request for the session editor; the id makes every open a new dialog.</summary>
internal sealed record EditorRequest(Guid RequestId, SessionEditorMode Mode, SessionProfile? Existing)
{
    /// <summary>Creates a request with a fresh identity.</summary>
    public static EditorRequest Create(SessionEditorMode mode, SessionProfile? profile) =>
        new(Guid.NewGuid(), mode, profile);
}

/// <summary>
/// Root component: title bar, navigation pane with the saved sessions, tabbed
/// workspaces (or the settings page) and the status bar. The shell only knows
/// <see cref="IProtocolSession"/> and capability flags, so a new protocol shows
/// up here without any shell change.
/// </summary>
internal sealed class MainShell : Component
{
    private static readonly object[] NoDependencies = Array.Empty<object>();

    public override Element Render()
    {
        var services = AppServices.EnsureInitialized();
        var workspace = services.Workspace;
        var snapshot = UseExternalStore(workspace.Subscribe, () => workspace.Snapshot);
        var settings = UseExternalStore(services.Settings.Subscribe, () => services.Settings.Current);

        // The localization service bumps Revision on every language change; the
        // root element is keyed by it so the whole tree rebuilds with new text.
        var localization = services.Localization;
        var localeRevision = UseExternalStore(localization.Subscribe, () => localization.Revision);

        // WinUI terminates the process when an exception escapes a UI callback;
        // record it before that happens so the crash is diagnosable.
        UseEffect(
            () =>
            {
                Microsoft.UI.Xaml.Application.Current.UnhandledException += OnUnhandledException;
                return () => Microsoft.UI.Xaml.Application.Current.UnhandledException -= OnUnhandledException;
            },
            NoDependencies);

        var (section, setSection) = UseState(ShellSection.Workspace);
        var (paneOpen, setPaneOpen) = UseState(true);
        var (editor, setEditor) = UseState<EditorRequest?>(null);
        var lastEditor = UseRef<Element?>(null);
        var connectPending = UseRef(false);

        void OpenEditor(SessionEditorMode mode, SessionProfile? profile)
        {
            setSection(ShellSection.Workspace);
            setEditor(EditorRequest.Create(mode, profile));
        }

        void OpenSettings() => setSection(ShellSection.Settings);

        void ShowAbout() => _ = services.Interaction.ShowInfoAsync(
            Str.T("app.aboutTitle", "About SurgeLink"),
            Str.T(
                "app.aboutBody",
                "A multi-protocol terminal for Windows. Terminal, SFTP and remote monitoring, built with WinUI 3 + Reactor."));

        void Connect(SessionProfile profile) => _ = ConnectAsync(profile);

        async Task ConnectAsync(SessionProfile profile, ConnectOptions? suppliedOptions = null)
        {
            if (connectPending.Current || workspace.Snapshot.IsConnecting)
            {
                return;
            }

            // Include the password prompt in the guard, before the store starts connecting.
            connectPending.Current = true;
            setSection(ShellSection.Workspace);
            try
            {
                var options = suppliedOptions is { Password: not null } ||
                              profile.Authentication == AuthenticationMethod.PublicKey && suppliedOptions is not null
                    ? suppliedOptions
                    : await BuildConnectOptionsAsync(services, profile);
                if (options is null)
                {
                    return;
                }

                await workspace.OpenAsync(profile, options, CancellationToken.None);
            }
            catch (ProtocolException ex)
            {
                await services.Interaction.ShowErrorAsync(
                    Str.T("shell.error.connectionFailed", "Connection failed"), ex.Message);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await services.Interaction.ShowErrorAsync(
                    Str.T("shell.error.unexpected", "Unexpected error"), ex.Message);
            }
            finally
            {
                connectPending.Current = false;
            }
        }

        void OpenDemo()
        {
            setSection(ShellSection.Workspace);
            var profile = workspace.Snapshot.Profiles.FirstOrDefault(p => p.ProtocolId == "demo")
                          ?? SessionProfile.New(
                              "demo",
                              "demo-host",
                              0,
                              Environment.UserName,
                              Str.T("shell.demo.profileName", "Demo session"));
            workspace.SaveProfile(profile, new StoredCredential { Password = "demo" });
            Connect(profile);
        }

        void SaveProfile(SessionEditorOutcome outcome)
        {
            setEditor(null);
            workspace.SaveProfile(outcome.Profile, outcome.Credential);
            if (outcome.ConnectAfterSave)
            {
                _ = ConnectAsync(outcome.Profile, outcome.Options);
            }
        }

        void DeleteProfile(SessionProfile profile) => _ = DeleteProfileAsync(profile);

        async Task DeleteProfileAsync(SessionProfile profile)
        {
            var confirmed = await services.Interaction.RequestConfirmationAsync(
                new ConfirmationRequest(
                    Str.T("shell.confirm.deleteSession.title", "Delete session"),
                    Str.F(
                        "shell.confirm.deleteSession.body",
                        "Delete '{0}'? The saved password is removed as well. Open tabs stay open.",
                        profile.Name),
                    Str.T("common.delete", "Delete"),
                    Str.T("common.cancel", "Cancel")),
                CancellationToken.None);

            if (confirmed)
            {
                workspace.DeleteProfile(profile.Id);
            }
        }

        void CloseTab(WorkspaceTab tab) => _ = CloseTabAsync(tab);

        async Task CloseTabAsync(WorkspaceTab tab)
        {
            if (settings.ConfirmCloseConnectedTab && tab.State == ConnectionState.Connected)
            {
                var confirmed = await services.Interaction.RequestConfirmationAsync(
                    new ConfirmationRequest(
                        Str.T("shell.confirm.closeTab.title", "Close session"),
                        Str.F("shell.confirm.closeTab.body", "'{0}' is still connected. Close it?", tab.Title),
                        Str.T("common.close", "Close"),
                        Str.T("shell.confirm.closeTab.keepOpen", "Keep open")),
                    CancellationToken.None);

                if (!confirmed)
                {
                    return;
                }
            }

            await workspace.CloseAsync(tab.Id);
        }

        void CloseActiveTab()
        {
            if (workspace.Snapshot.ActiveTab is { } active)
            {
                CloseTab(active);
            }
        }

        void CycleTab(int delta)
        {
            var tabs = workspace.Snapshot.Tabs;
            if (tabs.Count < 2)
            {
                return;
            }

            var current = 0;
            for (var index = 0; index < tabs.Count; index++)
            {
                if (tabs[index].Id == workspace.Snapshot.ActiveTabId)
                {
                    current = index;
                    break;
                }
            }

            var next = ((current + delta) % tabs.Count + tabs.Count) % tabs.Count;
            workspace.Activate(tabs[next].Id);
        }

        void Reconnect(WorkspaceTab tab) => _ = ReconnectAsync(tab);

        async Task ReconnectAsync(WorkspaceTab tab)
        {
            try
            {
                await workspace.ReconnectAsync(tab.Id);
            }
            catch (ProtocolException ex)
            {
                await services.Interaction.ShowErrorAsync(
                    Str.T("shell.error.reconnectFailed", "Reconnect failed"), ex.Message);
            }
            catch (Exception ex)
            {
                await services.Interaction.ShowErrorAsync(
                    Str.T("shell.error.reconnectFailed", "Reconnect failed"), ex.Message);
            }
        }

        void OpenEditorForProfile(SessionProfile? profile) =>
            OpenEditor(profile is null ? SessionEditorMode.Create : SessionEditorMode.Edit, profile);

        // Title bar -----------------------------------------------------------
        var activeTab = snapshot.ActiveTab;
        var subtitle = section == ShellSection.Settings
            ? Str.T("shell.subtitle.settings", "Settings")
            : snapshot.IsConnecting
                ? Str.T("shell.subtitle.connecting", "Connecting\u2026")
                : activeTab is null
                    ? Str.T("shell.subtitle.noSession", "No session open")
                    : $"{activeTab.Title}  \u00b7  {AppTheme.StatusText(activeTab.State)}  \u00b7  {activeTab.Session.HostInfo.Summary}";

        var titleBar = TitleBarView.Render(new TitleBarProps(
            subtitle,
            snapshot.IsConnecting,
            () => setPaneOpen(!paneOpen),
            () => OpenEditor(SessionEditorMode.Create, null),
            OpenSettings,
            ShowAbout));

        // Sidebar -------------------------------------------------------------
        var sidebar = Component<SidebarView, SidebarProps>(new SidebarProps(
            snapshot,
            Connect,
            OpenEditorForProfile,
            profile => OpenEditor(SessionEditorMode.Edit, profile),
            profile => OpenEditor(SessionEditorMode.Duplicate, profile),
            DeleteProfile,
            OpenDemo));

        // Content -------------------------------------------------------------
        var workspaceSurface = WorkspaceView.Render(new WorkspaceProps(
            snapshot,
            OpenEditorForProfile,
            Connect,
            CloseTab,
            tab => workspace.Activate(tab.Id),
            OpenDemo));

        Element BuildContent() => Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Star()],
            workspaceSurface.IsVisible(section == ShellSection.Workspace).Grid(row: 0),
            Component<SettingsPage>().IsVisible(section == ShellSection.Settings).Grid(row: 0));

        var navigation = NavigationView(
                [NavItem(Str.T("shell.nav.sessions", "Sessions"), "\uE8A9", "workspace")],
                BuildContent())
            .PaneDisplayMode(NavigationViewPaneDisplayMode.Left)
            // In "Left" display mode WinUI keeps the pane permanently expanded, so
            // the shell's toggle hides it explicitly instead of relying on IsPaneOpen.
            .PaneVisible(paneOpen)
            .IsPaneOpen(true, _ => { })
            .OpenPaneLength(AppTheme.SidebarWidth)
            .PaneTitle("SurgeLink")
            .PaneCustomContent(sidebar)
            .FooterMenuItems([NavItem(Str.T("shell.nav.settings", "Settings"), "\uE713", "settings")])
            .ItemInvoked(tag => HandleNavigation(tag, setSection))
            .Set(view => view.IsSettingsVisible = false) with
        {
            SelectedTag = section == ShellSection.Settings ? "settings" : "workspace",
        };

        // Status bar ----------------------------------------------------------
        var statusBar = StatusBarView.Render(new StatusBarProps(snapshot, settings, Reconnect));

        // Overlays: the session editor and the interaction dialog host --------
        // The editor element stays mounted after it closes: WinUI has already
        // closed the dialog, and removing a ContentDialog inside its own close
        // callback fails fast, so re-rendering the same element is a no-op.
        Element editorOverlay;
        if (editor is null)
        {
            editorOverlay = lastEditor.Current ?? Empty();
        }
        else
        {
            lastEditor.Current = Component<SessionEditorDialog, SessionEditorProps>(new SessionEditorProps(
                    editor.Mode,
                    editor.Existing,
                    SaveProfile,
                    () => setEditor(null)))
                .WithKey(editor.RequestId.ToString("n"));
            editorOverlay = lastEditor.Current;
        }

        var root = Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            titleBar.Grid(row: 0),
            navigation.Grid(row: 1),
            statusBar.Grid(row: 2),
            editorOverlay.Grid(row: 0, rowSpan: 3),
            Component<DialogHost>().Grid(row: 0, rowSpan: 3));

        var commands = ShellCommands.Build(
            () => OpenEditor(SessionEditorMode.Create, null),
            CloseActiveTab,
            () => CycleTab(1),
            () => CycleTab(-1),
            OpenSettings);

        // Keying the root by the locale revision rebuilds the whole tree in the new
        // language; sessions and terminal state live in the stores, not in the tree.
        return CommandHost(commands, root)
            .RequestedTheme(ThemeController.ToElementTheme(settings.Theme))
            .WithKey("locale-" + localeRevision);
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args) =>
        CrashLog.Write("WinUI", args.Exception);

    private static void HandleNavigation(string? tag, Action<ShellSection> setSection)
    {
        switch (tag)
        {
            case "settings":
                setSection(ShellSection.Settings);
                break;
            case "workspace":
                setSection(ShellSection.Workspace);
                break;
        }
    }

    private static async Task<ConnectOptions?> BuildConnectOptionsAsync(AppServices services, SessionProfile profile)
    {
        var stored = services.Credentials.Get(profile.Id);

        switch (profile.Authentication)
        {
            case AuthenticationMethod.PublicKey:
                return new ConnectOptions
                {
                    PrivateKeyPath = profile.PrivateKeyPath,
                    PrivateKeyPassphrase = stored?.PrivateKeyPassphrase,
                };

            case AuthenticationMethod.KeyboardInteractive:
                return new ConnectOptions();

            default:
                if (!string.IsNullOrEmpty(stored?.Password))
                {
                    return new ConnectOptions { Password = stored.Password };
                }

                if (profile.ProtocolId == "demo")
                {
                    return new ConnectOptions { Password = "demo" };
                }

                var secret = await services.Interaction.RequestSecretAsync(
                    new SecretRequest(
                        Str.F("shell.prompt.password.title", "Password for {0}@{1}", profile.Username, profile.Host),
                        Str.T("shell.prompt.password.body",
                            "This login is not saved. Enter the password for this session."),
                        IsPassword: true),
                    CancellationToken.None);

                return secret is null ? null : new ConnectOptions { Password = secret };
        }
    }
}
