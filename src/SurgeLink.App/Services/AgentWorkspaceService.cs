using System.Text.Json;
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
    private readonly AgentGateway _gateway = new();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, EventHandler> _humanHandlers = new();
    private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);
    private readonly Queue<AgentActivity> _activity = new();
    private readonly Dictionary<Guid, string> _handles = new();
    private static readonly JsonSerializerOptions ConfigJson = new() { WriteIndented = true };
    private AgentPipeServer? _server;
    private long _revision;
    private long _activityId;
    private string? _error;

    public AgentWorkspaceService(WorkspaceStore workspace, IUiDispatcher dispatcher, ILogger logger)
    {
        _workspace = workspace;
        _dispatcher = dispatcher;
        _logger = logger;
        _gateway.OperationCompleted += OnOperationCompleted;
        workspace.SessionClosing += OnSessionClosingAsync;
        McpHostPath = LocateExecutable("agent", "SurgeLink.AgentHost", "surgelink-agent-host.exe");
        CliPath = LocateExecutable("cli", "SurgeLink.Cli", "surgelink-cli.exe");
    }

    public long Revision => Interlocked.Read(ref _revision);
    public event EventHandler? Changed;
    public string McpHostPath { get; }
    public string CliPath { get; }
    public string? Error { get { lock (_gate) return _error; } }
    public IReadOnlyList<AgentActivity> Activity { get { lock (_gate) return _activity.Reverse().ToArray(); } }
    public AgentAccessMode GetMode(Guid tabId) => _gateway.GetMode(tabId.ToString());
    public bool IsShared(Guid tabId) => GetMode(tabId) != AgentAccessMode.None;
    public bool HasAccess => _workspace.Snapshot.Tabs.Any(t => IsShared(t.Id));

    public string McpConfiguration => JsonSerializer.Serialize(new
    {
        mcpServers = new { surgelink = new { command = McpHostPath, args = new[] { "--instance", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) } } },
    }, ConfigJson);

    public Action Subscribe(Action listener)
    {
        EventHandler handler = (_, _) => listener();
        Changed += handler;
        return () => Changed -= handler;
    }

    public async Task SetAccessAsync(WorkspaceTab tab, AgentAccessMode mode)
    {
        try
        {
            if (GetMode(tab.Id) == mode) return;
            var revision = tab.Terminal?.ReadScreen().InputRevision;
            await RevokeAsync(tab, "revoked");
            if (mode == AgentAccessMode.None) return;
            if (!_workspace.Snapshot.Tabs.Contains(tab) || tab.State != ConnectionState.Connected || tab.IsClosing) return;
            if (mode == AgentAccessMode.Control && (tab.Terminal is null || tab.Terminal.IsClosed
                || tab.Terminal.ReadScreen().InputRevision != revision)) return;
            _server ??= new AgentPipeServer(AgentEndpoint.ForProcess(Environment.ProcessId), _gateway);
            var handle = _gateway.GrantSession(tab.Id.ToString(), tab.Title, tab.Session, null,
                terminal: tab.Terminal, allowControl: mode == AgentAccessMode.Control,
                activate: (view, ct) => ActivateAsync(tab, view, ct),
                isActive: () => _workspace.Snapshot.ActiveTabId == tab.Id);
            lock (_gate)
            {
                _labels[handle] = tab.Title;
                _handles[tab.Id] = handle;
                _error = null;
                if (mode == AgentAccessMode.Control && tab.Terminal is { } terminal)
                {
                    EventHandler handler = (_, _) => { _ = RevokeAsync(tab, "human_input"); };
                    _humanHandlers[tab.Id] = handler;
                    terminal.UserInput += handler;
                }
            }
            Record(tab.Title, "access", mode == AgentAccessMode.Control ? "control_enabled" : "read_enabled");
        }
        catch (Exception ex) { ReportError(ex); }
    }

    public async Task StopAllAsync()
    {
        // Invoke all revocations before waiting: one slow transport must not retain other handles.
        await Task.WhenAll(_workspace.Snapshot.Tabs.Select(tab => RevokeAsync(tab, "revoked"))).ConfigureAwait(false);
    }

    private async Task RevokeAsync(WorkspaceTab tab, string reason)
    {
        try
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
        catch (Exception ex) { ReportError(ex); }
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
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent)
        {
            if (!File.Exists(Path.Combine(root.FullName, "SurgeLink.slnx"))) continue;
            foreach (var config in new[] { "Debug", "Release" })
                foreach (var platform in new[] { "", "x64", "ARM64" })
                {
                    var candidate = Path.Combine(root.FullName, "src", project, "bin", platform, config, "net10.0", executable);
                    if (File.Exists(candidate)) return candidate;
                }
            break;
        }
        return installed;
    }

    public async ValueTask DisposeAsync()
    {
        _workspace.SessionClosing -= OnSessionClosingAsync;
        await StopAllAsync().ConfigureAwait(false);
        if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);
        _gateway.OperationCompleted -= OnOperationCompleted;
    }
}
