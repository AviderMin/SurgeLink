using Microsoft.Extensions.Logging;
using SurgeLink.Agent;
using SurgeLink.App.Sessions;
using SurgeLink.Core.Sessions;

namespace SurgeLink.App.Services;

public sealed record AgentActivity(long Id, DateTimeOffset Time, string Session, string Operation, string Outcome);

/// <summary>Desktop access scope and visible PTY adapter. Execution policy belongs to the calling Agent.</summary>
public sealed class AgentWorkspaceService : IAsyncDisposable
{
    private readonly WorkspaceStore _workspace;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    // The desktop's Allow reading/Control grant includes file reads. Per-operation
    // execution policy belongs to the external Agent app; no extra desktop prompt.
    private readonly AgentGateway _gateway = new((_, _) => Task.FromResult(true));
    private readonly object _gate = new();
    private readonly SemaphoreSlim _accessChanges = new(1, 1);
    private long _stopGeneration;
    private bool _disposed;
    private Task? _disposeTask;
    private readonly Dictionary<Guid, EventHandler> _humanHandlers = new();
    private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);
    private readonly Queue<AgentActivity> _activity = new();
    private readonly Dictionary<Guid, string> _handles = new();
    private readonly string _pipeName;
    private AgentPipeServer? _server;
    private long _revision;
    private long _accessRevision;
    private long _activityId;
    private string? _error;

    public AgentWorkspaceService(WorkspaceStore workspace, IUiDispatcher dispatcher, ILogger logger, string? pipeName = null)
    {
        _pipeName = pipeName ?? AgentEndpoint.ForProcess(Environment.ProcessId);
        _workspace = workspace;
        _dispatcher = dispatcher;
        _logger = logger;
        _gateway.OperationCompleted += OnOperationCompleted;
        _gateway.McpPeersChanged += Notify;
        workspace.SessionClosing += OnSessionClosingAsync;
        workspace.TerminalReplacing += OnTerminalReplacingAsync;
        McpHostPath = LocateExecutable("agent", "SurgeLink.AgentHost", "surgelink-agent-host.exe");
        CliPath = LocateExecutable("cli", "SurgeLink.Cli", "surgelink-cli.exe");
        // Connection discovery is available before any session grant. An empty grant list
        // exposes no terminal, file, monitoring or credential data.
        try { _server = new AgentPipeServer(_pipeName, _gateway); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ReportError(ex); }
    }

    public long Revision => Interlocked.Read(ref _revision);
    /// <summary>Only access changes invalidate tab indicators; ordinary Agent requests do not.</summary>
    public long AccessRevision => Interlocked.Read(ref _accessRevision);
    public event EventHandler? Changed;
    public string McpHostPath { get; }
    public string CliPath { get; }
    public bool McpHostAvailable => File.Exists(McpHostPath);
    public IReadOnlyList<AgentMcpPeer> McpPeers => _gateway.McpPeers;
    public string? Error { get { lock (_gate) return _error; } }
    public IReadOnlyList<AgentActivity> Activity { get { lock (_gate) return _activity.Reverse().ToArray(); } }
    public AgentAccessMode GetMode(Guid tabId) => _gateway.GetMode(tabId.ToString());
    public bool IsShared(Guid tabId) => GetMode(tabId) != AgentAccessMode.None;
    public bool HasAccess => _workspace.Snapshot.Tabs.Any(t => IsShared(t.Id));

    public string McpConfiguration => AgentMcpSetup.Configuration(McpHostPath);

    public Action Subscribe(Action listener)
    {
        EventHandler handler = (_, _) => listener();
        Changed += handler;
        return () => Changed -= handler;
    }

    public async Task SetAccessAsync(WorkspaceTab tab, AgentAccessMode mode)
    {
        if (mode == AgentAccessMode.None)
        {
            lock (_gate) _stopGeneration++;
            try { await RevokeAsync(tab, "revoked"); }
            catch (Exception ex) { ReportError(ex); }
            return;
        }
        long generation;
        lock (_gate) { if (_disposed) return; generation = _stopGeneration; }
        var revision = tab.Terminal?.ReadScreen().InputRevision;
        await _accessChanges.WaitAsync();
        try
        {
            if (GetMode(tab.Id) == mode) return;
            await RevokeAsync(tab, "revoked");
            lock (_gate)
            {
                if (_disposed || generation != _stopGeneration || !_workspace.Snapshot.Tabs.Contains(tab)
                    || tab.State != ConnectionState.Connected || tab.IsClosing || tab.IsReopeningTerminal
                    || tab.Terminal?.ReadScreen().InputRevision != revision) return;
                if (mode == AgentAccessMode.Control && (tab.Terminal is null || tab.Terminal.IsClosed)) return;
                _server ??= new AgentPipeServer(_pipeName, _gateway);
                var handle = _gateway.GrantSession(tab.Id.ToString(), tab.Title, tab.Session, null,
                    terminal: tab.Terminal, allowControl: mode == AgentAccessMode.Control,
                    activate: (view, ct) => ActivateAsync(tab, view, ct),
                    isActive: () => _workspace.Snapshot.ActiveTabId == tab.Id,
                    target: new AgentSessionTarget(tab.Session.SessionId, tab.Profile.Host,
                        tab.Session.Protocol.UsesNetworkAddress ? tab.Profile.ResolvePort(tab.Session.Protocol.DefaultPort) : 0,
                        tab.Profile.Username));
                _labels[handle] = tab.Title;
                _handles[tab.Id] = handle;
                _error = null;
                if (mode == AgentAccessMode.Control && tab.Terminal is { } terminal)
                {
                    EventHandler handler = (_, _) => { _ = RevokeForHumanAsync(tab); };
                    _humanHandlers[tab.Id] = handler;
                    terminal.UserInput += handler;
                }
            }
            // Input may land between the earlier revision check and event subscription.
            // Once subscribed, subsequent input revokes through the handler as usual.
            if (mode == AgentAccessMode.Control && tab.Terminal?.ReadScreen().InputRevision != revision)
            {
                await RevokeAsync(tab, "human_input");
                return;
            }
            Record(tab.Title, "access", mode == AgentAccessMode.Control ? "control_enabled" : "read_enabled");
        }
        catch (Exception ex) { ReportError(ex); }
        finally { _accessChanges.Release(); }
    }

    public async Task StopAllAsync()
    {
        lock (_gate) _stopGeneration++;
        // Invoke all revocations before waiting: one slow transport must not retain other handles.
        try { await Task.WhenAll(_workspace.Snapshot.Tabs.Select(tab => RevokeAsync(tab, "revoked"))).ConfigureAwait(false); }
        catch (Exception ex) { ReportError(ex); throw; }
    }

    private async Task RevokeForHumanAsync(WorkspaceTab tab)
    {
        try { await RevokeAsync(tab, "human_input").ConfigureAwait(false); }
        catch (Exception ex) { ReportError(ex); }
    }

    private async Task RevokeAsync(WorkspaceTab tab, string reason)
    {
        var shared = IsShared(tab.Id);
        string? oldHandle;
        lock (_gate)
        {
            _handles.Remove(tab.Id, out oldHandle);
            if (_humanHandlers.Remove(tab.Id, out var handler) && tab.Terminal is { } terminal)
                terminal.UserInput -= handler;
        }
        var drain = _gateway.RevokeAsync(tab.Id.ToString());
        if (shared) Record(tab.Title, "access", reason);
        await drain.ConfigureAwait(false);
        if (oldHandle is not null) lock (_gate) _labels.Remove(oldHandle);
    }

    private async Task ActivateAsync(WorkspaceTab tab, string view, CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (tab.IsClosing || !_workspace.Snapshot.Tabs.Contains(tab)) throw new InvalidOperationException("Connection is closing.");
                var kind = view switch
                {
                    "terminal" when tab.Terminal is not null => SessionViewKind.Terminal,
                    "files" when tab.Session.FileSystem is not null => SessionViewKind.Files,
                    "monitor" when tab.Session.Monitor is not null => SessionViewKind.Monitor,
                    _ => throw new NotSupportedException(),
                };
                _workspace.Activate(tab.Id);
                _workspace.SetView(tab.Id, kind);
                completion.TrySetResult();
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        await completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private Task OnSessionClosingAsync(WorkspaceTab tab) => RevokeAsync(tab, "connection_closed");
    private Task OnTerminalReplacingAsync(WorkspaceTab tab) => RevokeAsync(tab, "shell_replaced");

    private void OnOperationCompleted(string method, string? handle, string outcome)
    {
        string label;
        lock (_gate) label = handle is not null && _labels.TryGetValue(handle, out var name) ? name : "";
        // Payloads/commands are visible in the PTY, never copied into logs or the activity history.
        _logger.LogInformation("Agent operation {Method} session {Handle}: {Outcome}", method, handle, outcome);
        Record(label, method, outcome);
    }

    private void Record(string label, string method, string outcome)
    {
        if (method == "access") Interlocked.Increment(ref _accessRevision);
        lock (_gate)
        {
            _activity.Enqueue(new(++_activityId, DateTimeOffset.UtcNow, label, method, outcome));
            while (_activity.Count > 20) _activity.Dequeue();
        }
        Notify();
    }

    private void ReportError(Exception error)
    {
        _logger.LogError(error, "Agent access failed");
        lock (_gate) _error = "access_failed";
        Notify();
    }

    private void Notify()
    {
        Interlocked.Increment(ref _revision);
        _dispatcher.Post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    private static string LocateExecutable(string installedFolder, string project, string executable)
    {
        var installed = Path.Combine(AppContext.BaseDirectory, installedFolder, executable);
        if (File.Exists(installed)) return installed;
#if DEBUG
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent)
        {
            if (!File.Exists(Path.Combine(root.FullName, "SurgeLink.slnx"))) continue;
            var platform = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64 ? "ARM64" : "x64";
            var candidate = (from buildPlatform in new[] { platform, "" }
                             let path = Path.Combine(root.FullName, "src", project, "bin", buildPlatform, "Debug", "net10.0", executable)
                             where File.Exists(path)
                             select path).FirstOrDefault();
            if (candidate is not null) return candidate;
            break;
        }
#endif
        return installed;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { await StopAllAsync().ConfigureAwait(false); }
        finally
        {
            _workspace.SessionClosing -= OnSessionClosingAsync;
            _workspace.TerminalReplacing -= OnTerminalReplacingAsync;
            try { if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                _gateway.OperationCompleted -= OnOperationCompleted;
                _gateway.McpPeersChanged -= Notify;
            }
        }
    }
}
