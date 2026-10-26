using System.Security.Cryptography;
using SurgeLink.Agent;

namespace SurgeLink.AgentHost;

/// <summary>Re-discovers the desktop for each call, including after restarts. Presence never grants access.</summary>
public sealed class DesktopConnection(int? instance = null, Func<int>? resolveDesktop = null,
    Func<IReadOnlyList<AgentDesktopProcess>>? discoverDesktops = null)
{
    private readonly Func<int> _resolveDesktop = resolveDesktop ?? (discoverDesktops is null
        ? () => AgentEndpoint.ResolveDesktopProcess()
        : () => AgentEndpoint.SelectDesktopProcess(discoverDesktops().Select(p => p.ProcessId).ToArray()));
    private readonly Func<IReadOnlyList<AgentDesktopProcess>> _discoverDesktops = discoverDesktops ?? AgentEndpoint.DiscoverDesktopProcesses;
    private readonly object _orderedGate = new();
    private Task _orderedOperations = Task.CompletedTask;
    private Task? _selectionBarrier;
    private int _selectedDesktop = instance ?? 0;
    private readonly string _clientId = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _clientName = "MCP";
    private int _lastDesktop;
    private SelectedDesktop? _selectedIdentity;

    private int ResolveDesktop()
    {
        if (Volatile.Read(ref _selectedDesktop) is > 0 and var selected)
        {
            var identity = Volatile.Read(ref _selectedIdentity);
            if (identity is null || identity.StartedUtc is null)
            {
                var current = KnownDesktops().FirstOrDefault(p => p.ProcessId == selected);
                if (current?.StartedUtc is not null) Volatile.Write(ref _selectedIdentity, new(selected, current.StartedUtc));
            }
            return selected;
        }
        return _resolveDesktop();
    }

    public void ClientConnected(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && name.Length <= 80 && !name.Any(char.IsControl))
            Volatile.Write(ref _clientName, name);
        _connected.TrySetResult();
    }

    public Task<AgentResponse> SendAsync(AgentRequest request, CancellationToken ct)
    {
        if (request.Method == "desktops.list") return SendCoreAsync(request, ct);
        if (request.Method is not ("desktops.select" or "terminal.text" or "terminal.key"))
        {
            Task? selection;
            lock (_orderedGate) selection = _selectionBarrier;
            return selection is not null && !selection.IsCompleted
                ? AfterSelection(selection, () => SendCoreAsync(request, ct))
                : SendCoreAsync(request, ct);
        }

        lock (_orderedGate)
        {
            var next = RunOrdered(_orderedOperations, () => SendCoreAsync(request, ct));
            _orderedOperations = next;
            if (request.Method == "desktops.select") _selectionBarrier = next;
            _ = next.ContinueWith(finished =>
            {
                _ = finished.Exception;
                lock (_orderedGate)
                {
                    if (ReferenceEquals(_orderedOperations, finished)) _orderedOperations = Task.CompletedTask;
                    if (ReferenceEquals(_selectionBarrier, finished)) _selectionBarrier = null;
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return next;
        }
    }

    private static async Task<AgentResponse> AfterSelection(Task selection, Func<Task<AgentResponse>> operation)
    {
        try { await selection.ConfigureAwait(false); }
        catch { /* Selection errors belong to that caller; a later request re-discovers safely. */ }
        return await operation().ConfigureAwait(false);
    }

    private static async Task<AgentResponse> RunOrdered(Task previous, Func<Task<AgentResponse>> operation)
    {
        try { await previous.ConfigureAwait(false); }
        catch { /* The earlier caller observes its own error; do not strand later operations. */ }
        return await operation().ConfigureAwait(false);
    }

    private async Task<AgentResponse> SendCoreAsync(AgentRequest request, CancellationToken ct)
    {
        var desktop = 0;
        try
        {
            // A selection is queued behind earlier terminal input; later input is queued behind
            // the selection, so it cannot overtake a write to the previous desktop.
            if (request.Method == "desktops.list") return await ListDesktopsAsync(ct).ConfigureAwait(false);
            if (request.Method == "desktops.select") return await SelectDesktopAsync(request.DesktopProcessId, ct).ConfigureAwait(false);
            desktop = ResolveDesktop();
            if (CurrentDesktop() is { StartedUtc: not null } initial && Volatile.Read(ref _selectedIdentity)?.StartedUtc is { } started
                && initial.StartedUtc != started)
                return DiscoveryError("desktop_instance_expired", "The selected desktop process ID was reused by a new desktop. Call list_desktops and explicitly select the intended replacement before retrying. Terminal grants never carry over to the replacement; uncertain input must not be replayed.", [desktop]);
            return await new AgentClient(AgentEndpoint.ForProcess(desktop)).SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (AgentDesktopDiscoveryException ex) { return DiscoveryError(ex.ErrorCode, ex.Message, ex.ProcessIds); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return AgentResponse.Error("cancelled", "The request was cancelled."); }
        catch (OperationCanceledException)
        { return DiscoveryError("desktop_ipc_timeout", "The desktop did not respond before the request deadline. For uncertain terminal input, preserve the original operation ID and arguments; observe before retrying."); }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            var running = KnownDesktops();
            var expired = SelectedInstanceExpired(running, desktop);
            return DiscoveryError(expired ? "desktop_instance_expired" : "desktop_ipc_unavailable",
                expired
                    ? "The selected or startup-bound desktop has exited. Call list_desktops and select_desktop with its intended replacement, or call select_desktop without processId to reset to automatic discovery. Do not edit saved client files or read credentials."
                    : "The desktop process exists but its IPC endpoint cannot be reached. Call list_desktops to inspect reachability; confirm the desktop and MCP Host run under the same Windows account. Do not replay uncertain input under a new operation ID.",
                running.Select(p => p.ProcessId).ToArray());
        }
    }

    private AgentDesktopProcess? CurrentDesktop()
    {
        var identity = Volatile.Read(ref _selectedIdentity);
        return identity is null ? null : KnownDesktops().FirstOrDefault(p => p.ProcessId == identity.ProcessId);
    }

    private bool SelectedInstanceExpired(IReadOnlyList<AgentDesktopProcess> running, int desktop)
    {
        if (Volatile.Read(ref _selectedDesktop) <= 0) return false;
        var current = running.FirstOrDefault(p => p.ProcessId == desktop);
        var identity = Volatile.Read(ref _selectedIdentity);
        return current is null || identity?.StartedUtc is not null && current.StartedUtc is not null && current.StartedUtc != identity.StartedUtc;
    }

    private IReadOnlyList<AgentDesktopProcess> KnownDesktops()
    {
        try { return _discoverDesktops(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        { return []; }
    }

    private AgentResponse DiscoveryError(string code, string message, IReadOnlyList<int>? processIds = null) => new(false,
        System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            desktopProcessIds = processIds ?? KnownDesktops().Select(p => p.ProcessId).ToArray(),
            selectedProcessId = Volatile.Read(ref _selectedDesktop) is > 0 and var selected ? (int?)selected : null,
            nextTool = "list_desktops",
        }, AgentJson.Options), code, message);

    private async Task<AgentResponse> ListDesktopsAsync(CancellationToken ct)
    {
        var candidates = _discoverDesktops();
        var desktops = await Task.WhenAll(candidates.Take(32).Select(async candidate =>
        {
            var sessions = await ProbeAsync(candidate.ProcessId, ct).ConfigureAwait(false);
            var reachable = sessions?.Success == true;
            var count = reachable ? sessions!.Data!.Value.GetArrayLength() : (int?)null;
            var controlCount = reachable ? sessions!.Data!.Value.EnumerateArray().Count(s => s.GetProperty("terminalControl").GetBoolean()) : (int?)null;
            return new { candidate.ProcessId, candidate.ExecutablePath,
                selected = Volatile.Read(ref _selectedDesktop) == candidate.ProcessId,
                reachable, sharedSessionCount = count, controlSessionCount = controlCount,
                startedUtc = candidate.StartedUtc, processIdReused = SelectedInstanceExpired([candidate], candidate.ProcessId) && candidate.StartedUtc != Volatile.Read(ref _selectedIdentity)?.StartedUtc };
        })).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return AgentResponse.Ok(new
        {
            desktops, automatic = Volatile.Read(ref _selectedDesktop) == 0, truncated = candidates.Count > 32,
            selectedProcessId = Volatile.Read(ref _selectedDesktop) is > 0 and var selected ? (int?)selected : null,
            instruction = "Select the intended processId using select_desktop, then call list_sessions. Counts describe existing grants only; selecting never grants access. If several targets are plausible, ask the user instead of guessing. No local configuration or credential files are needed.",
        });
    }

    private async Task<AgentResponse> SelectDesktopAsync(int? processId, CancellationToken ct)
    {
        if (processId is <= 0) return AgentResponse.Error("invalid_arguments", "processId must be a positive integer from list_desktops.");
        AgentDesktopProcess? replacement = null;
        if (processId is int pid)
        {
            var candidates = _discoverDesktops();
            replacement = candidates.FirstOrDefault(p => p.ProcessId == pid);
            if (replacement is null)
                return DiscoveryError("desktop_not_found", "That processId is no longer a running SurgeLink desktop. Call list_desktops again.", candidates.Select(p => p.ProcessId).ToArray());
            var probe = await ProbeAsync(pid, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (probe?.Success != true)
                return DiscoveryError("desktop_ipc_unavailable", "That desktop's IPC endpoint is not reachable. Selection was not changed; call list_desktops to inspect other instances.");
        }
        // Probe and identity come from the same discovery result, not two inconsistent snapshots.
        Volatile.Write(ref _selectedIdentity, processId is int selectedPid ? new(selectedPid, replacement?.StartedUtc) : null);
        var previous = Interlocked.Exchange(ref _selectedDesktop, processId ?? 0);
        if (previous != (processId ?? 0)) await RemovePresenceAsync(Interlocked.Exchange(ref _lastDesktop, 0)).ConfigureAwait(false);
        return AgentResponse.Ok(new { selectedProcessId = processId, automatic = processId is null,
            instruction = "Desktop routing changed for this MCP connection only. Call list_sessions and verify the intended session before each terminal input. Existing session authorization is unchanged." });
    }

    private static async Task<AgentResponse?> ProbeAsync(int pid, CancellationToken ct)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(2));
            return await new AgentClient(AgentEndpoint.ForProcess(pid)).SendAsync(new("sessions.list"), budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { return null; }
    }

    private async Task RemovePresenceAsync(int desktop)
    {
        if (desktop <= 0) return;
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await new AgentClient(AgentEndpoint.ForProcess(desktop)).SendAsync(new("client.disconnect", ClientId: _clientId), budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { }
    }

    public async Task RunPresenceAsync(CancellationToken ct)
    {
        try
        {
            await _connected.Task.WaitAsync(ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            do
            {
                try
                {
                    var desktop = ResolveDesktop();
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    budget.CancelAfter(TimeSpan.FromSeconds(4));
                    var result = await SendAsync(new("client.heartbeat", ClientId: _clientId,
                        ClientName: Volatile.Read(ref _clientName)), budget.Token).ConfigureAwait(false);
                    if (result.Success) Volatile.Write(ref _lastDesktop, desktop);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or PlatformNotSupportedException)
                { /* Desktop restart/offline is recoverable; tool discovery must still succeed. */ }
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            await RemovePresenceAsync(Volatile.Read(ref _lastDesktop)).ConfigureAwait(false);
        }
    }

    private sealed record SelectedDesktop(int ProcessId, DateTimeOffset? StartedUtc);
}
