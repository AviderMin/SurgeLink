using Microsoft.Extensions.Logging;
using SurgeLink.App.Services;
using SurgeLink.Core.Files;
using SurgeLink.Core.Protocols;
using SurgeLink.Core.Security;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Storage;

namespace SurgeLink.App.Sessions;

/// <summary>Which of a session's capability views is showing.</summary>
public enum SessionViewKind
{
    Terminal,
    Files,
    Monitor,
}

/// <summary>One open session tab.</summary>
public sealed class WorkspaceTab
{
    internal WorkspaceTab(
        Guid id,
        SessionProfile profile,
        IProtocolSession session,
        TerminalBridge? terminal,
        FileTransferQueue transfers)
    {
        Id = id;
        Profile = profile;
        Session = session;
        Terminal = terminal;
        Transfers = transfers;
        State = session.State;
    }

    public Guid Id { get; }

    public SessionProfile Profile { get; }

    public IProtocolSession Session { get; }

    public TerminalBridge? Terminal { get; internal set; }

    public bool IsReopeningTerminal { get; internal set; }

    /// <summary>
    /// Session-owned transfer queue. It survives Files/Monitor/Terminal switches
    /// and locale rebuilds, and is disposed when the tab closes.
    /// </summary>
    public FileTransferQueue Transfers { get; }

    public ConnectionState State { get; internal set; }

    public string? Message { get; internal set; }

    /// <summary>True when output arrived while this tab was not active; cleared when it becomes active.</summary>
    public bool HasActivity { get; internal set; }

    public SessionViewKind View { get; internal set; } = SessionViewKind.Terminal;

    /// <summary>Last directory browsed in the Files view; retained across view switches.</summary>
    public string? FileBrowserPath { get; internal set; }

    public string Title => Profile.Name;

    /// <summary>Prevents new external access once connection teardown starts.</summary>
    public bool IsClosing { get; internal set; }

    internal EventHandler<SessionStateChangedEventArgs>? StateChangedHandler { get; set; }
    internal bool ConnectionReleased { get; set; }
}

/// <summary>Immutable view of the workspace used by the shell to render.</summary>
public sealed record WorkspaceSnapshot(
    IReadOnlyList<SessionProfile> Profiles,
    IReadOnlyList<WorkspaceTab> Tabs,
    Guid? ActiveTabId,
    string SearchText,
    bool IsConnecting,
    long Revision = 0)
{
    /// <summary>Initial empty workspace.</summary>
    public static WorkspaceSnapshot Empty { get; } = new(
        Array.Empty<SessionProfile>(),
        Array.Empty<WorkspaceTab>(),
        null,
        string.Empty,
        false);

    /// <summary>Distinct connected hosts, independent of users, ports and tab count.</summary>
    public int ConnectedServerCount => Tabs
        .Where(tab => tab.State == ConnectionState.Connected
            && !string.Equals(tab.Profile.ProtocolId, "demo", StringComparison.OrdinalIgnoreCase))
        .Select(tab => NormalizeHost(tab.Profile.Host))
        .Where(host => host.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    private static string NormalizeHost(string host)
    {
        var value = host.Trim().TrimEnd('.').Trim('[', ']');
        return System.Net.IPAddress.TryParse(value, out var address) ? address.ToString() : value;
    }

    public WorkspaceTab? ActiveTab =>
        ActiveTabId is Guid id ? Tabs.FirstOrDefault(t => t.Id == id) : null;

    /// <summary>Profiles matching the current search text, newest first.</summary>
    public IReadOnlyList<SessionProfile> FilteredProfiles
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                return Profiles.OrderByDescending(p => p.LastUsedUtc ?? DateTimeOffset.MinValue).ThenBy(p => p.Name, StringComparer.CurrentCulture).ToList();
            }

            var query = SearchText.Trim();
            return Profiles
                .Where(p => p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                            || p.Host.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                            || p.Username.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .OrderByDescending(p => p.LastUsedUtc ?? DateTimeOffset.MinValue)
                .ThenBy(p => p.Name, StringComparer.CurrentCulture)
                .ToList();
        }
    }
}

/// <summary>
/// Single source of truth for saved profiles and open sessions. The UI renders
/// from <see cref="Snapshot"/> and calls the mutating methods; connections are
/// created through <see cref="ISessionManager"/> so this class stays protocol agnostic.
/// </summary>
public sealed class WorkspaceStore
{
    private readonly ISessionManager _sessions;
    private readonly ISessionStore _profiles;
    private readonly ICredentialStore _credentials;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<WorkspaceStore> _logger;
    private readonly IAppSettingsStore? _settings;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (TaskCompletionSource Completion, bool KeepTab)> _closing = new();
    private WorkspaceSnapshot _snapshot = WorkspaceSnapshot.Empty;

    /// <summary>Creates the store and loads saved profiles.</summary>
    public WorkspaceStore(
        ISessionManager sessions,
        ISessionStore profiles,
        ICredentialStore credentials,
        IUiDispatcher dispatcher,
        ILogger<WorkspaceStore> logger, IAppSettingsStore? settings = null)
    {
        _sessions = sessions;
        _profiles = profiles;
        _credentials = credentials;
        _dispatcher = dispatcher;
        _logger = logger;
        _settings = settings;
        _snapshot = _snapshot with { Profiles = _profiles.Load() };
    }

    /// <summary>Raised after every state change, always on the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Revoke and drain external operations before any protocol resources are disposed.</summary>
    public event Func<WorkspaceTab, Task>? SessionClosing;
    public event Func<WorkspaceTab, Task>? TerminalReplacing;

    /// <summary>Current immutable snapshot.</summary>
    public WorkspaceSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Subscription helper shaped for <c>UseExternalStore</c>.</summary>
    public Action Subscribe(Action listener)
    {
        EventHandler handler = (_, _) => listener();
        Changed += handler;
        return () => Changed -= handler;
    }

    /// <summary>Opens a session in a new tab. Throws <see cref="ProtocolException"/> on failure.</summary>
    public Task<WorkspaceTab> OpenAsync(SessionProfile profile, ConnectOptions? options, CancellationToken cancellationToken) =>
        OpenCoreAsync(profile, options, cancellationToken);

    private async Task<WorkspaceTab> OpenCoreAsync(
        SessionProfile profile,
        ConnectOptions? options,
        CancellationToken cancellationToken,
        WorkspaceTab? reconnecting = null)
    {
        Update(s => s with { IsConnecting = true }, force: true);
        IProtocolSession? session = null;
        TerminalBridge? terminal = null;
        WorkspaceTab? openingTab = null;
        try
        {
            if (reconnecting?.Session.RequiresDisconnectBeforeReconnect == true)
            {
                await CloseAsync(reconnecting.Id, replacement: null, keepTab: true);
                cancellationToken.ThrowIfCancellationRequested();
            }
            session = await _sessions.OpenAsync(profile, options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (session.Terminal is { } remoteTerminal)
            {
                terminal = new TerminalBridge(remoteTerminal, _dispatcher, _logger, settings: _settings,
                    terminalType: TerminalTypeFor(profile),
                    existingEmulator: reconnecting?.Terminal?.Emulator.CopyHistoryForNewShell(),
                    banner: reconnecting is null ? null : "--- Reconnected ---");
                await terminal.StartAsync(cancellationToken);
            }

            var tab = new WorkspaceTab(Guid.NewGuid(), profile, session, terminal, new FileTransferQueue(_logger));
            openingTab = tab;
            // Bind to the tab before the host probe. It is not published in the
            // workspace yet, but disconnects during initialization still matter.
            tab.StateChangedHandler = (_, args) => OnSessionStateChanged(tab, args);
            session.StateChanged += tab.StateChangedHandler;
            OnSessionStateChanged(tab, new SessionStateChangedEventArgs(tab.State, session.State));
            await ProbeHostInfoAsync(tab, session, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (terminal is not null)
            {
                terminal.Closed += (_, _) => OnTerminalClosed(tab, terminal);
                terminal.Activity += (_, _) => OnTerminalActivity(tab, terminal);
            }

            // Merge only connection metadata into a still-existing, matching profile.
            // Serialize this read/modify/write with profile edits and deletion.
            lock (_gate)
            {
                var current = _profiles.Load().FirstOrDefault(p => p.Id == profile.Id);
                if (current is not null && SameConnection(current, profile))
                {
                    var updated = current.MarkUsed(DateTimeOffset.UtcNow);
                    if (!string.IsNullOrWhiteSpace(session.HostInfo.OperatingSystem))
                    {
                        updated = updated with { DetectedOperatingSystem = session.HostInfo.OperatingSystem };
                    }
                    _profiles.Upsert(updated);
                }
            }

            if (reconnecting is not null)
            {
                if (session.State != ConnectionState.Connected)
                    throw new ProtocolException("The replacement session disconnected while reconnecting.");
                ReloadProfiles();
                cancellationToken.ThrowIfCancellationRequested();
                await CloseAsync(reconnecting.Id, tab);
            }
            else
            {
                Update(s => s with
                {
                    Profiles = LoadProfilesFromDisk(),
                    Tabs = [.. s.Tabs, tab],
                    ActiveTabId = tab.Id,
                });
            }

            _logger.LogInformation("Session {Name} opened", profile.Name);
            if (terminal?.IsClosed == true) { OnTerminalClosed(tab, terminal); }
            return tab;
        }
        catch
        {
            if (openingTab is not null)
            {
                openingTab.IsClosing = true;
                openingTab.Session.StateChanged -= openingTab.StateChangedHandler;
                try { await openingTab.Transfers.DisposeAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Opening queue cleanup failed."); }
            }
            if (terminal is not null)
            {
                try { await terminal.DisposeAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Opening terminal cleanup failed."); }
            }
            if (session is not null)
            {
                try { await _sessions.CloseAsync(session.SessionId); }
                catch (Exception ex) { _logger.LogDebug(ex, "Opening session cleanup failed."); }
            }
            throw;
        }
        finally
        {
            Update(s => s with { IsConnecting = false }, force: true);
        }
    }

    /// <summary>Closes a tab and disposes its connection.</summary>
    public Task CloseAsync(Guid tabId) => CloseAsync(tabId, null);

    private Task CloseAsync(Guid tabId, WorkspaceTab? replacement, bool keepTab = false)
    {
        WorkspaceTab? tab;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_closing.TryGetValue(tabId, out var existing))
            {
                if (replacement is not null || keepTab) return Task.FromCanceled(new CancellationToken(canceled: true));
                return existing.KeepTab ? CloseAfterTeardownAsync(tabId, existing.Completion.Task) : existing.Completion.Task;
            }
            tab = _snapshot.Tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab is null)
            {
                return replacement is null && !keepTab ? Task.CompletedTask : Task.FromCanceled(new CancellationToken(canceled: true));
            }
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _closing.Add(tabId, (completion, keepTab));
            tab.IsClosing = true;
        }
        _ = CloseTabCoreAsync(tab, completion, replacement, keepTab);
        return completion.Task;
    }

    private async Task CloseAfterTeardownAsync(Guid tabId, Task teardown)
    {
        await teardown.ConfigureAwait(false);
        await CloseAsync(tabId).ConfigureAwait(false);
    }

    private async Task CloseTabCoreAsync(WorkspaceTab tab, TaskCompletionSource completion, WorkspaceTab? replacement, bool keepTab)
    {
        try
        {
            if (!tab.ConnectionReleased)
            {
                if (SessionClosing is { } closing)
                    foreach (Func<WorkspaceTab, Task> handler in closing.GetInvocationList())
                        await handler(tab).ConfigureAwait(false);

                tab.Session.StateChanged -= tab.StateChangedHandler;
                // Attempt every cleanup even if an earlier resource fails to close.
                try { await tab.Transfers.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Transfer queue cleanup failed for {TabId}", tab.Id); }
                try { if (tab.Terminal is not null) await tab.Terminal.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Terminal cleanup failed for {TabId}", tab.Id); }
                try { await _sessions.CloseAsync(tab.Session.SessionId).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Connection cleanup failed for {TabId}", tab.Id); }
            }

            Update(s =>
            {
                _closing.Remove(tab.Id);
                tab.ConnectionReleased = true;
                if (keepTab)
                {
                    tab.IsClosing = false;
                    if (tab.State != ConnectionState.Faulted) tab.State = ConnectionState.Disconnected;
                    return s;
                }
                if (replacement is not null)
                {
                    return s with
                    {
                        Tabs = s.Tabs.Select(item => item.Id == tab.Id ? replacement : item).ToArray(),
                        ActiveTabId = s.ActiveTabId == tab.Id ? replacement.Id : s.ActiveTabId,
                    };
                }
                var remaining = s.Tabs.Where(t => t.Id != tab.Id).ToList();
                var activeTabId = s.ActiveTabId == tab.Id ? remaining.LastOrDefault()?.Id : s.ActiveTabId;
                // Closing the active tab activates another one implicitly; it must
                // not keep an indicator that only applies to background tabs.
                if (activeTabId is Guid nextActiveId)
                {
                    var nextActive = remaining.FirstOrDefault(t => t.Id == nextActiveId);
                    if (nextActive is not null) { nextActive.HasActivity = false; }
                }

                return s with { Tabs = remaining, ActiveTabId = activeTabId };
            }, force: keepTab);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            // A failed pre-close drain must keep the live connection retryable.
            lock (_gate)
            {
                tab.IsClosing = false;
                _closing.Remove(tab.Id);
                completion.TrySetException(ex);
            }
        }
    }

    /// <summary>
    /// Drops the connection behind a tab and opens the same profile again in place,
    /// preserving the tab position and terminal scrollback history.
    /// </summary>
    public async Task ReconnectAsync(Guid tabId, ConnectOptions? options = null, CancellationToken cancellationToken = default)
    {
        var tab = Snapshot.Tabs.FirstOrDefault(item => item.Id == tabId);
        if (tab is null || tab.IsClosing) { return; }
        var profile = GetReconnectProfile(tabId);
        if (profile is null) { return; }
        cancellationToken.ThrowIfCancellationRequested();
        await OpenCoreAsync(profile, options, cancellationToken, reconnecting: tab);
    }

    /// <summary>Replaces only the exited shell, retaining the connection, files, monitor and transfers.</summary>
    public async Task ReopenTerminalAsync(Guid tabId, CancellationToken cancellationToken = default)
    {
        WorkspaceTab? tab;
        lock (_gate)
        {
            tab = _snapshot.Tabs.FirstOrDefault(item => item.Id == tabId);
            if (tab is null || tab.IsClosing || tab.IsReopeningTerminal || tab.State != ConnectionState.Connected
                || tab.Terminal is not { IsClosed: true } || !tab.Session.CanReopenTerminal) { return; }
            tab.IsReopeningTerminal = true;
        }
        Update(snapshot => snapshot, force: true);
        TerminalBridge? replacement = null;
        try
        {
            if (TerminalReplacing is { } replacing)
                foreach (Func<WorkspaceTab, Task> handler in replacing.GetInvocationList()) { await handler(tab); }
            cancellationToken.ThrowIfCancellationRequested();
            var remote = await tab.Session.ReopenTerminalAsync(cancellationToken);
            replacement = new TerminalBridge(remote, _dispatcher, _logger, settings: _settings,
                terminalType: TerminalTypeFor(tab.Profile),
                existingEmulator: tab.Terminal?.Emulator,
                banner: "--- Shell reopened ---");
            await replacement.StartAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var previous = tab.Terminal;
            var opened = replacement;
            lock (_gate)
            {
                if (tab.IsClosing || !_snapshot.Tabs.Contains(tab) || tab.Session.State != ConnectionState.Connected)
                    throw new OperationCanceledException("The session closed while opening the shell.");
                tab.Terminal = opened;
                tab.Message = opened.IsClosed ? "Remote shell exited." : null;
                opened.Closed += (_, _) => OnTerminalClosed(tab, opened);
                opened.Activity += (_, _) => OnTerminalActivity(tab, opened);
                replacement = null;
            }
            if (opened.IsClosed) { OnTerminalClosed(tab, opened); }
            if (previous is not null) { await previous.DisposeAsync(); }
        }
        finally
        {
            try { if (replacement is not null) { await replacement.DisposeAsync(); } }
            finally
            {
                lock (_gate) { tab.IsReopeningTerminal = false; }
                Update(snapshot => snapshot, force: true);
            }
        }
    }

    /// <summary>Uses the latest saved configuration, or the tab configuration if it was deleted.</summary>
    public SessionProfile? GetReconnectProfile(Guid tabId)
    {
        lock (_gate)
        {
            var tab = _snapshot.Tabs.FirstOrDefault(t => t.Id == tabId);
            return tab is null ? null : _profiles.Load().FirstOrDefault(p => p.Id == tab.Profile.Id) ?? tab.Profile;
        }
    }

    /// <summary>Reads the generic TERM option a profile carries for its protocol.</summary>
    private static string? TerminalTypeFor(SessionProfile profile) =>
        profile.Options.TryGetValue("terminalType", out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static bool SameConnection(SessionProfile left, SessionProfile right) =>
        left.ProtocolId == right.ProtocolId && left.Host == right.Host && left.Port == right.Port
        && left.Username == right.Username && left.Authentication == right.Authentication
        && left.PrivateKeyPath == right.PrivateKeyPath && left.Options.Count == right.Options.Count
        && left.Options.All(pair => right.Options.TryGetValue(pair.Key, out var value) && value == pair.Value);

    /// <summary>Makes a tab active and clears its new-output indicator.</summary>
    public void Activate(Guid tabId)
    {
        var tab = Snapshot.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null)
        {
            return;
        }

        var clearedActivity = tab.HasActivity;
        tab.HasActivity = false;
        Update(s => s.Tabs.Any(t => t.Id == tabId) ? s with { ActiveTabId = tabId } : s, force: clearedActivity);
    }

    /// <summary>Remembers the last directory browsed by the Files view for one tab.</summary>
    public void SetFileBrowserPath(Guid tabId, string path)
    {
        var tab = Snapshot.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null || string.Equals(tab.FileBrowserPath, path, StringComparison.Ordinal))
        {
            return;
        }

        tab.FileBrowserPath = path;
        Update(s => s, force: true);
    }

    /// <summary>Switches which capability view a tab shows.</summary>
    public void SetView(Guid tabId, SessionViewKind view)
    {
        var tab = Snapshot.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null || tab.View == view)
        {
            return;
        }

        tab.View = view;
        Update(s => s, force: true);
    }

    /// <summary>Sets the sidebar filter text.</summary>
    public void SetSearchText(string text) =>
        Update(s => s with { SearchText = text ?? string.Empty });

    /// <summary>Adds or updates a saved profile.</summary>
    public void SaveProfile(SessionProfile profile, StoredCredential? credential = null)
    {
        lock (_gate)
        {
            var previous = _profiles.Load().FirstOrDefault(item => item.Id == profile.Id);
            if (previous is not null &&
                (!string.Equals(previous.Host, profile.Host, StringComparison.OrdinalIgnoreCase)
                 || previous.Port != profile.Port || previous.ProtocolId != profile.ProtocolId))
            {
                profile = profile with { DetectedOperatingSystem = null };
            }
            _profiles.Upsert(profile);
            if (credential is { DecryptionFailed: true })
            {
                // The stored secret could not be decrypted. Leave the existing
                // ciphertext untouched instead of throwing or deleting the user's
                // data: the profile edit still commits and the UI still refreshes.
            }
            else if (credential is not null)
            {
                _credentials.Save(profile.Id, credential);
            }
            else
            {
                _credentials.Delete(profile.Id);
            }

            Update(s => s with { Profiles = LoadProfilesFromDisk() });
        }
    }

    /// <summary>Deletes a saved profile and its stored secret.</summary>
    public void DeleteProfile(string profileId)
    {
        lock (_gate)
        {
            _profiles.Delete(profileId);
            _credentials.Delete(profileId);
            Update(s => s with { Profiles = LoadProfilesFromDisk() });
        }
    }

    /// <summary>Re-reads profiles from disk.</summary>
    public void ReloadProfiles() => Update(s => s with { Profiles = LoadProfilesFromDisk() });

    private IReadOnlyList<SessionProfile> LoadProfilesFromDisk() => _profiles.Load();

    private async Task ProbeHostInfoAsync(WorkspaceTab tab, IProtocolSession session, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var info = await session.LoadHostInfoAsync(cts.Token).WaitAsync(cts.Token);
            _logger.LogInformation("Host info for {Name}: {Summary}", tab.Profile.Name, info.Summary);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Host info probe failed for {Name}", tab.Profile.Name);
        }
    }

    private void OnSessionStateChanged(WorkspaceTab tab, SessionStateChangedEventArgs e)
    {
        _dispatcher.Post(() =>
        {
            if (tab.IsClosing) { return; }

            tab.State = e.NewState;
            tab.Message = e.Message;
            if (e.NewState is ConnectionState.Disconnected or ConnectionState.Faulted)
            {
                // No file system is reachable any more; cancel work rather than
                // letting the queue keep retrying against a dead session.
                _ = tab.Transfers.DisposeAsync().AsTask();
            }

            if (Snapshot.Tabs.Contains(tab)) { Update(s => s, force: true); }
        });
    }

    private void OnTerminalActivity(WorkspaceTab tab, TerminalBridge terminal)
    {
        // TerminalBridge raises Activity on the UI thread after coalescing output chunks.
        // A final output notification may already be queued when the remote shell
        // raises Closed; keep it as long as this is still the tab's terminal.
        if (tab.IsClosing || tab.State != ConnectionState.Connected
            || !ReferenceEquals(tab.Terminal, terminal)) { return; }

        var snapshot = Snapshot;
        if (tab.HasActivity || snapshot.ActiveTabId == tab.Id || !snapshot.Tabs.Contains(tab)) { return; }

        tab.HasActivity = true;
        Update(s => s, force: true);
    }

    private void OnTerminalClosed(WorkspaceTab tab, TerminalBridge terminal)
    {
        if (tab.IsClosing || tab.State != ConnectionState.Connected
            || !Snapshot.Tabs.Contains(tab) || !ReferenceEquals(tab.Terminal, terminal)) { return; }
        // TerminalBridge dispatches Closed to the UI thread, so state can be updated directly.
        tab.Message = "Remote shell exited.";
        Update(s => s, force: true);
    }

    private void Update(Func<WorkspaceSnapshot, WorkspaceSnapshot> mutate, bool force = false)
    {
        bool changed;
        lock (_gate)
        {
            var next = mutate(_snapshot);
            if (!force && ReferenceEquals(next, _snapshot))
            {
                return;
            }

            // The revision makes in-place mutations of WorkspaceTab observable to
            // UseExternalStore, whose default comparer compares the record by value.
            _snapshot = next with { Revision = _snapshot.Revision + 1 };
            changed = true;
        }

        if (changed)
        {
            Raise();
        }
    }

    private void Raise()
    {
        if (_dispatcher.IsOnUiThread)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _dispatcher.Post(() => Changed?.Invoke(this, EventArgs.Empty));
        }
    }
}
