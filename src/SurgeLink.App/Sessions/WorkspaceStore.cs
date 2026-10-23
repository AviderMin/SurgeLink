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

    public TerminalBridge? Terminal { get; }

    /// <summary>
    /// Session-owned transfer queue. It survives Files/Monitor/Terminal switches
    /// and locale rebuilds, and is disposed when the tab closes.
    /// </summary>
    public FileTransferQueue Transfers { get; }

    public ConnectionState State { get; internal set; }

    public string? Message { get; internal set; }

    public SessionViewKind View { get; internal set; } = SessionViewKind.Terminal;

    /// <summary>Last directory browsed in the Files view; retained across view switches.</summary>
    public string? FileBrowserPath { get; internal set; }

    public string Title => Profile.Name;
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
    private readonly object _gate = new();
    private WorkspaceSnapshot _snapshot = WorkspaceSnapshot.Empty;

    /// <summary>Creates the store and loads saved profiles.</summary>
    public WorkspaceStore(
        ISessionManager sessions,
        ISessionStore profiles,
        ICredentialStore credentials,
        IUiDispatcher dispatcher,
        ILogger<WorkspaceStore> logger)
    {
        _sessions = sessions;
        _profiles = profiles;
        _credentials = credentials;
        _dispatcher = dispatcher;
        _logger = logger;
        _snapshot = _snapshot with { Profiles = _profiles.Load() };
    }

    /// <summary>Raised after every state change, always on the UI thread.</summary>
    public event EventHandler? Changed;

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
    public async Task<WorkspaceTab> OpenAsync(SessionProfile profile, ConnectOptions? options, CancellationToken cancellationToken)
    {
        Update(s => s with { IsConnecting = true }, force: true);
        IProtocolSession? session = null;
        TerminalBridge? terminal = null;
        WorkspaceTab? openingTab = null;
        try
        {
            session = await _sessions.OpenAsync(profile, options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (session.Terminal is { } remoteTerminal)
            {
                terminal = new TerminalBridge(remoteTerminal, _dispatcher, _logger);
                await terminal.StartAsync(cancellationToken);
            }

            var tab = new WorkspaceTab(Guid.NewGuid(), profile, session, terminal, new FileTransferQueue(_logger));
            openingTab = tab;
            session.StateChanged += OnSessionStateChanged;
            await ProbeHostInfoAsync(tab, session, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (terminal is not null)
            {
                terminal.Closed += (_, _) => OnTerminalClosed(tab);
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

            Update(s => s with
            {
                Profiles = LoadProfilesFromDisk(),
                Tabs = [.. s.Tabs, tab],
                ActiveTabId = tab.Id,
            });

            _logger.LogInformation("Session {Name} opened", profile.Name);
            return tab;
        }
        catch
        {
            if (session is not null) { session.StateChanged -= OnSessionStateChanged; }
            if (openingTab is not null)
            {
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
    public async Task CloseAsync(Guid tabId)
    {
        WorkspaceTab? tab;
        lock (_gate)
        {
            tab = _snapshot.Tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab is null)
            {
                return;
            }
        }

        tab.Session.StateChanged -= OnSessionStateChanged;

        // Stop the queue before the protocol session and its file system are
        // disposed; the queue waits for its worker so no transfer keeps using a
        // dead connection.
        await tab.Transfers.DisposeAsync().ConfigureAwait(false);

        if (tab.Terminal is not null)
        {
            await tab.Terminal.DisposeAsync();
        }

        await _sessions.CloseAsync(tab.Session.SessionId);

        Update(s =>
        {
            var remaining = s.Tabs.Where(t => t.Id != tabId).ToList();
            var active = s.ActiveTabId == tabId
                ? remaining.LastOrDefault()?.Id
                : s.ActiveTabId;
            return s with { Tabs = remaining, ActiveTabId = active };
        });
    }

    /// <summary>
    /// Drops the connection behind a tab and opens the same profile again.
    /// The tab is replaced (same position is not guaranteed, the new tab becomes active).
    /// </summary>
    public async Task ReconnectAsync(Guid tabId, ConnectOptions? options = null, CancellationToken cancellationToken = default)
    {
        WorkspaceTab? tab;
        lock (_gate)
        {
            tab = _snapshot.Tabs.FirstOrDefault(t => t.Id == tabId);
        }

        if (tab is null)
        {
            return;
        }

        var profile = GetReconnectProfile(tabId);
        if (profile is null) { return; }
        cancellationToken.ThrowIfCancellationRequested();
        await CloseAsync(tabId);
        await OpenAsync(profile, options, cancellationToken);
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

    private static bool SameConnection(SessionProfile left, SessionProfile right) =>
        left.ProtocolId == right.ProtocolId && left.Host == right.Host && left.Port == right.Port
        && left.Username == right.Username && left.Authentication == right.Authentication
        && left.PrivateKeyPath == right.PrivateKeyPath && left.Options.Count == right.Options.Count
        && left.Options.All(pair => right.Options.TryGetValue(pair.Key, out var value) && value == pair.Value);

    /// <summary>Makes a tab active.</summary>
    public void Activate(Guid tabId) =>
        Update(s => s.Tabs.Any(t => t.Id == tabId) ? s with { ActiveTabId = tabId } : s);

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
            if (credential is not null)
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

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        if (sender is not IProtocolSession session)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            var tab = Snapshot.Tabs.FirstOrDefault(t => t.Session.SessionId == session.SessionId);
            if (tab is null)
            {
                return;
            }

            tab.State = e.NewState;
            tab.Message = e.Message;
            if (e.NewState is ConnectionState.Disconnected or ConnectionState.Faulted)
            {
                // No file system is reachable any more; cancel work rather than
                // letting the queue keep retrying against a dead session.
                _ = tab.Transfers.DisposeAsync().AsTask();
            }

            Update(s => s, force: true);
        });
    }

    private void OnTerminalClosed(WorkspaceTab tab)
    {
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
