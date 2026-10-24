using Microsoft.Extensions.Logging;
using SurgeLink.Agent;
using SurgeLink.App.Localization;
using SurgeLink.App.Sessions;
using SurgeLink.Core.Sessions;

namespace SurgeLink.App.Services;

/// <summary>UI-owned consent and adapter to the independent Agent gateway.</summary>
public sealed class AgentWorkspaceService : IAsyncDisposable
{
    private readonly WorkspaceStore _workspace;
    private readonly InteractionService _interaction;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly AgentGateway _gateway;
    private AgentPipeServer? _server;
    private long _revision;

    public AgentWorkspaceService(WorkspaceStore workspace, InteractionService interaction, IUiDispatcher dispatcher, ILogger logger)
    {
        _workspace = workspace;
        _interaction = interaction;
        _dispatcher = dispatcher;
        _logger = logger;
        _gateway = new AgentGateway(ConfirmFileAsync);
        _gateway.OperationCompleted += (method, handle, outcome) =>
            _logger.LogInformation("Agent operation {Method} session {Handle}: {Outcome}", method, handle, outcome);
        workspace.SessionClosing += OnSessionClosingAsync;
    }

    public long Revision => Interlocked.Read(ref _revision);
    public event EventHandler? Changed;
    public bool IsShared(Guid tabId) => _gateway.IsShared(tabId.ToString());
    public Action Subscribe(Action listener)
    {
        EventHandler handler = (_, _) => listener();
        Changed += handler;
        return () => Changed -= handler;
    }

    public async Task ToggleAsync(WorkspaceTab tab)
    {
        try
        {
            if (IsShared(tab.Id))
            {
                await _gateway.RevokeAsync(tab.Id.ToString());
                _logger.LogInformation("Agent sharing revoked for tab {TabId}", tab.Id);
            }
            else
            {
                var allowed = await _interaction.RequestConfirmationAsync(new ConfirmationRequest(
                    Str.T("shell.agent.enableTitle", "Allow local CLI access"),
                    Str.F("shell.agent.enableBody", "Allow programs running as your Windows user to read session '{0}', including its host information, terminal screen and system metrics? File requests require separate confirmation. Contents may contain secrets and an external agent may send them to its model provider. Access ends when you revoke it or close this connection. Desktop instance: {1}.", tab.Title, Environment.ProcessId),
                    Str.T("shell.agent.allow", "Allow access")), CancellationToken.None);
                if (!allowed) return;
                if (!_workspace.Snapshot.Tabs.Contains(tab) || tab.State != ConnectionState.Connected || tab.IsClosing) return;
                _server ??= new AgentPipeServer(AgentEndpoint.ForProcess(Environment.ProcessId), _gateway);
                _gateway.GrantSession(tab.Id.ToString(), tab.Title, tab.Session, tab.Terminal is { } terminal
                    ? () => { lock (terminal.Emulator.SyncRoot) return terminal.Emulator.GetVisibleText(); }
                    : null);
                _logger.LogInformation("Agent sharing enabled for tab {TabId}", tab.Id);
            }
            Notify();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not change Agent sharing");
            await _interaction.ShowErrorAsync(Str.T("shell.agent.errorTitle", "CLI access unavailable"),
                Str.T("shell.agent.errorBody", "Could not change CLI access. Check the application log and try again."));
        }
    }

    private async Task<bool> ConfirmFileAsync(AgentFileAccess access, CancellationToken ct)
    {
        // Marshal translation and dialog creation to the UI thread as localization state is UI-owned.
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var allowed = await _interaction.RequestConfirmationAsync(new ConfirmationRequest(
                    Str.T("shell.agent.fileTitle", "Allow CLI file access"),
                    Str.F("shell.agent.fileBody", "Session: {0}\nOperation: {1}\nRemote path: {2}\n\nAllow this one request? Symbolic links may resolve to another location. The result is returned to a local program and may be sent to an AI provider.", access.Label, access.Operation, access.Path),
                    Str.T("shell.agent.allow", "Allow access")), ct).WaitAsync(ct);
                completion.TrySetResult(allowed);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task OnSessionClosingAsync(WorkspaceTab tab)
    {
        await _gateway.RevokeAsync(tab.Id.ToString()).ConfigureAwait(false);
        Notify();
    }

    private void Notify()
    {
        Interlocked.Increment(ref _revision);
        _dispatcher.Post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public async ValueTask DisposeAsync()
    {
        _workspace.SessionClosing -= OnSessionClosingAsync;
        foreach (var tab in _workspace.Snapshot.Tabs)
            await _gateway.RevokeAsync(tab.Id.ToString()).ConfigureAwait(false);
        if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);
    }
}
