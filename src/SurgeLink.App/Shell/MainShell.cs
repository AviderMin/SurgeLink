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
        // Keep the root window across store-triggered renders, when Reactor's
        // ambient ActiveHost can be absent or refer to another nested host.
        var ownerWindow = UseWindow();
        var pickers = UseRef<WindowStoragePicker?>(null);
        if (pickers.Current is null && ownerWindow is not null)
        {
            pickers.Current = new WindowStoragePicker(ownerWindow.NativeWindow);
        }
        var services = AppServices.Current
            ?? throw new InvalidOperationException("AppServices must be initialized before the shell mounts.");
        var workspace = services.Workspace;
        var snapshot = UseExternalStore(workspace.Subscribe, () => workspace.Snapshot);
        var settings = UseExternalStore(services.Settings.Subscribe, () => services.Settings.Current);

        // Subscribe for translated shell text without remounting stateful workspaces.
        var localization = services.Localization;
        _ = UseExternalStore(localization.Subscribe, () => localization.Revision);

        // WinUI terminates the process when an exception escapes a UI callback;
        // record it before that happens so the crash is diagnosable.
        UseEffect(
            () =>
            {
                Microsoft.UI.Xaml.Application.Current.UnhandledException += OnUnhandledException;
                return () => Microsoft.UI.Xaml.Application.Current.UnhandledException -= OnUnhandledException;
            },
            NoDependencies);

        var (paneOpen, setPaneOpen) = UseState(true);
        var (editor, setEditor) = UseState<EditorRequest?>(null);
        var lastEditor = UseRef<Element?>(null);
        var (settingsOpen, setSettingsOpen) = UseState(false);
        var (settingsRequest, setSettingsRequest) = UseState(0);
        var (aboutOpen, setAboutOpen) = UseState(false);
        var (aboutRequest, setAboutRequest) = UseState(0);
        var shellModal = UseRef<string?>(null);
        var connectPending = UseRef(false);
        var connectionCancellation = UseRef<CancellationTokenSource?>(null);
        var (persistenceError, setPersistenceError) = UseState<string?>(null);

        void ReportPersistenceFailure(Exception error) => setPersistenceError(error.Message);

        bool TryPersist(Action operation)
        {
            try
            {
                operation();
                setPersistenceError(null);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException)
            {
                ReportPersistenceFailure(ex);
                // Profile and credential files may fail independently; show what
                // actually persisted even if the second write failed.
                workspace.ReloadProfiles();
                return false;
            }
        }

        async void CancelConnection()
        {
            try
            {
                if (connectionCancellation.Current is { } cancellation)
                    await cancellation.CancelAsync();
            }
            catch (Exception ex) { CrashLog.Write("CancelConnection", ex); }
        }

        void OpenEditor(SessionEditorMode mode, SessionProfile? profile)
        {
            if (shellModal.Current is not null || QueuedDialog.IsBusy(ownerWindow?.NativeWindow)) return;
            shellModal.Current = "editor";
            setEditor(EditorRequest.Create(mode, profile));
        }

        void OpenSettings()
        {
            if (shellModal.Current is not null || QueuedDialog.IsBusy(ownerWindow?.NativeWindow)) return;
            shellModal.Current = "settings";
            setSettingsRequest(settingsRequest + 1);
            setSettingsOpen(true);
        }
        void ShowAbout()
        {
            if (shellModal.Current is not null || QueuedDialog.IsBusy(ownerWindow?.NativeWindow)) return;
            shellModal.Current = "about";
            setAboutRequest(aboutRequest + 1);
            setAboutOpen(true);
        }
        void Connect(SessionProfile profile) => _ = ConnectAsync(profile);

        async Task ShowConnectionErrorAsync(string title, Exception error)
        {
            // The connect command is fire-and-forget; dialog failures must not
            // escape it or replace the original failure without a diagnostic.
            try { await services.Interaction.ShowErrorAsync(title, error.Message); }
            catch (Exception ex) { CrashLog.Write("ConnectionErrorDialog", ex); }
        }

        async Task ConnectAsync(SessionProfile profile, ConnectOptions? suppliedOptions = null, Guid? reconnectTabId = null)
        {
            if (connectPending.Current || workspace.Snapshot.IsConnecting)
            {
                return;
            }

            // Include the password prompt in the guard, before the store starts connecting.
            connectPending.Current = true;
            using var cancellation = new CancellationTokenSource();
            connectionCancellation.Current = cancellation;
            try
            {
                var options = suppliedOptions is { Password: not null } ||
                              profile.Authentication == AuthenticationMethod.PublicKey && suppliedOptions is not null
                    ? suppliedOptions
                    : await BuildConnectOptionsAsync(services, profile, cancellation.Token);
                if (options is null)
                {
                    return;
                }

                if (reconnectTabId is Guid tabId)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    await workspace.CloseAsync(tabId);
                }
                await Task.Run(() => workspace.OpenAsync(profile, options, cancellation.Token));
            }
            catch (ProtocolException) when (cancellation.IsCancellationRequested) { }
            catch (ProtocolException ex)
            {
                await ShowConnectionErrorAsync(
                    Str.T("shell.error.connectionFailed", "Connection failed"), ex);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                CrashLog.Write("Connect", ex);
                await ShowConnectionErrorAsync(
                    Str.T("shell.error.unexpected", "Unexpected error"), ex);
            }
            finally
            {
                connectPending.Current = false;
                connectionCancellation.Current = null;
            }
        }

        void OpenDemo()
        {
            var profile = workspace.Snapshot.Profiles.FirstOrDefault(p => p.ProtocolId == "demo")
                          ?? SessionProfile.New(
                              "demo",
                              "demo-host",
                              0,
                              Environment.UserName,
                              Str.T("shell.demo.profileName", "Demo session"));
            if (TryPersist(() => workspace.SaveProfile(profile, new StoredCredential { Password = "demo" })))
            {
                Connect(profile);
            }
        }

        void SaveProfile(SessionEditorOutcome outcome)
        {
            shellModal.Current = null;
            setEditor(null);
            if (TryPersist(() => workspace.SaveProfile(outcome.Profile, outcome.Credential))
                && outcome.ConnectAfterSave)
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
                TryPersist(() => workspace.DeleteProfile(profile.Id));
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
            if (QueuedDialog.IsBusy(ownerWindow?.NativeWindow)) return;
            if (workspace.Snapshot.ActiveTab is { } active)
            {
                CloseTab(active);
            }
        }

        void CycleTab(int delta)
        {
            if (QueuedDialog.IsBusy(ownerWindow?.NativeWindow)) return;
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

        void Reconnect(WorkspaceTab tab)
        {
            if (workspace.GetReconnectProfile(tab.Id) is { } profile)
            {
                _ = ConnectAsync(profile, reconnectTabId: tab.Id);
            }
        }

        void OpenEditorForProfile(SessionProfile? profile) =>
            OpenEditor(profile is null ? SessionEditorMode.Create : SessionEditorMode.Edit, profile);

        // Title bar -----------------------------------------------------------
        var connectedCount = snapshot.ConnectedServerCount;
        var subtitle = connectedCount == 0
            ? string.Empty
            : connectedCount == 1
                ? Str.F("shell.subtitle.connectedOne", "Connected to {0} server", connectedCount)
                : Str.F("shell.subtitle.connectedMany", "Connected to {0} servers", connectedCount);

        var titleBar = TitleBarView.Render(new TitleBarProps(
            subtitle,
            snapshot.IsConnecting,
            CancelConnection,
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
            rows: [GridSize.Auto, GridSize.Star()],
            (persistenceError is null ? Empty() : InfoBar(
                Str.T("shell.error.saveFailed", "Could not save changes"),
                Str.F("shell.error.saveFailedHint", "Check folder access and available disk space, then try again. {0}", persistenceError)) with
            {
                Severity = InfoBarSeverity.Error,
                IsClosable = true,
                OnClosed = () => setPersistenceError(null),
            }).Grid(row: 0),
            workspaceSurface.Grid(row: 1));

        // A bounded star row gives the session list the remaining pane height.
        // NavigationView.PaneCustomContent is measured in an Auto row instead.
        Element NavigationButton(string label, string glyph, Action onClick) =>
            Button(HStack(AppTheme.SpacingS, Icon(FontIcon(glyph, fontSize: 16)), Body(label)), onClick)
                .SubtleButton()
                .HAlign(HorizontalAlignment.Stretch)
                .Set(button => button.HorizontalContentAlignment = HorizontalAlignment.Left)
                .AutomationName(label);

        var pane = Border(Grid(
                columns: [GridSize.Star()],
                rows: [GridSize.Auto, GridSize.Star()],
                NavigationButton(Str.T("shell.nav.sessions", "Sessions"), "\uE8A9", () => setPaneOpen(true))
                    .Margin(AppTheme.SpacingM, AppTheme.SpacingS, AppTheme.SpacingM, 0).Grid(row: 0),
                sidebar.Grid(row: 1)))
            .Background(Theme.LayerFill)
            .BorderBrush(Theme.DividerStroke)
            .BorderThickness(0, 0, 1, 0)
            .IsVisible(paneOpen)
            .AutomationId("SessionSidebar");

        var navigation = Grid(
            columns: [GridSize.Px(paneOpen ? AppTheme.SidebarWidth : 0), GridSize.Star()],
            rows: [GridSize.Star()],
            pane.Grid(column: 0),
            BuildContent().Grid(column: 1));

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
                    () => { shellModal.Current = null; setEditor(null); }))
                .WithKey(editor.RequestId.ToString("n"));
            editorOverlay = lastEditor.Current;
        }

        var settingsOverlay = settingsOpen
            ? Component<SettingsDialog, SettingsDialogProps>(
                new SettingsDialogProps(() => { shellModal.Current = null; setSettingsOpen(false); }, ReportPersistenceFailure))
                .WithKey("settings-dialog-" + settingsRequest)
            : Empty();

        var aboutOverlay = aboutOpen
            ? Component<AboutDialog, AboutDialogProps>(new AboutDialogProps(() => { shellModal.Current = null; setAboutOpen(false); }))
                .WithKey("about-dialog-" + aboutRequest)
            : Empty();

        var root = Grid(
                columns: [GridSize.Star()],
                rows: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                titleBar.Grid(row: 0),
                navigation.Grid(row: 1),
                statusBar.Grid(row: 2),
                editorOverlay.Grid(row: 0, rowSpan: 3),
                settingsOverlay.Grid(row: 0, rowSpan: 3),
                aboutOverlay.Grid(row: 0, rowSpan: 3),
                Component<DialogHost>().Grid(row: 0, rowSpan: 3))
            // Provide on the visual root, not on the CommandHost decorator:
            // TabView content and dialog overlays must all see the same graph.
            .Provide(AppServices.ReactorContext, services)
            .Provide(WindowStoragePicker.Context, pickers.Current);

        var commands = ShellCommands.Build(
            () => OpenEditor(SessionEditorMode.Create, null),
            CloseActiveTab,
            () => CycleTab(1),
            () => CycleTab(-1),
            OpenSettings);

        // Keep component identities stable when the language changes.
        return CommandHost(commands, root)
            .RequestedTheme(ThemeController.ToElementTheme(settings.Theme));
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args) =>
        CrashLog.Write("WinUI", args.Exception);

    private static async Task<ConnectOptions?> BuildConnectOptionsAsync(AppServices services, SessionProfile profile, CancellationToken cancellationToken)
    {
        if (services.Protocols.Get(profile.ProtocolId).Descriptor.AuthenticatesInTerminal)
            return new ConnectOptions();

        var stored = services.Credentials.Get(profile.Id);

        switch (profile.Authentication)
        {
            case AuthenticationMethod.PublicKey:
                var passphrase = stored?.PrivateKeyPassphrase;
                if (passphrase is null)
                {
                    passphrase = await services.Interaction.RequestSecretAsync(
                        new SecretRequest(
                            Str.T("shell.prompt.passphrase.title", "Private key passphrase"),
                            Str.T("shell.prompt.passphrase.body", "Enter the key passphrase, or leave it empty for an unencrypted key.")),
                        cancellationToken);
                    if (passphrase is null) { return null; }
                }
                return new ConnectOptions
                {
                    PrivateKeyPath = profile.PrivateKeyPath,
                    PrivateKeyPassphrase = passphrase,
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
                    cancellationToken);

                return secret is null ? null : new ConnectOptions { Password = secret };
        }
    }
}
