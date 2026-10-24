using System.Security.Cryptography;
using SurgeLink.Core.Files;
using SurgeLink.Core.Sessions;
using SurgeLink.Core.Terminal;

namespace SurgeLink.Agent;

/// <summary>
/// Owns opt-in access to existing desktop connections. It never creates connections or reads credentials.
/// A grant is process-local, refers to exactly one connection, and is drained before that connection closes.
/// </summary>
public sealed partial class AgentGateway
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _revocations = new(StringComparer.Ordinal);
    private readonly Func<AgentFileAccess, CancellationToken, Task<bool>> _confirmFile;
    private readonly TimeProvider _timeProvider;

    /// <summary>File access is denied unless the host explicitly supplies an authorization policy.</summary>
    public AgentGateway(Func<AgentFileAccess, CancellationToken, Task<bool>>? confirmFile = null, TimeProvider? timeProvider = null)
    {
        _confirmFile = confirmFile ?? ((_, _) => Task.FromResult(false));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action<string, string?, string>? OperationCompleted;

    public string GrantSession(string connectionKey, string label, IProtocolSession session, Func<string>? readScreen,
        ITerminalAutomation? terminal = null, bool allowControl = false,
        Func<string, CancellationToken, Task>? activate = null, Func<bool>? isActive = null, AgentSessionTarget? target = null)
    {
        lock (_gate)
        {
            if (target is not null && (target.SessionId != session.SessionId || string.IsNullOrWhiteSpace(target.Server)))
                throw new ArgumentException("Target must identify this connection and its server/device.", nameof(target));
            if (_revocations.ContainsKey(connectionKey)) throw new InvalidOperationException("Previous access is still draining.");
            if (session.State != ConnectionState.Connected)
                throw new InvalidOperationException("Only connected sessions can be shared.");
            var existing = _grants.Values.FirstOrDefault(g => g.ConnectionKey == connectionKey);
            if (existing is not null) return existing.Info.Handle;
            var info = new AgentSessionInfo(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), label,
                session.Protocol.Id, session.HostInfo, readScreen is not null || terminal is not null,
                session.FileSystem is not null, session.Monitor is not null, allowControl && terminal is not null, Target: target);
            _grants.Add(info.Handle, new Grant(connectionKey, info, session, readScreen, terminal, activate, isActive));
            return info.Handle;
        }
    }

    public bool IsShared(string connectionKey)
    {
        lock (_gate) return _grants.Values.Any(g => g.ConnectionKey == connectionKey);
    }

    public AgentAccessMode GetMode(string connectionKey)
    {
        lock (_gate)
        {
            var grant = _grants.Values.FirstOrDefault(g => g.ConnectionKey == connectionKey);
            return grant is null ? AgentAccessMode.None : grant.Info.TerminalControl ? AgentAccessMode.Control : AgentAccessMode.Observe;
        }
    }

    public Task RevokeAsync(string connectionKey)
    {
        Grant[] removed;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_revocations.TryGetValue(connectionKey, out var pending)) return pending.Task;
            removed = _grants.Values.Where(g => g.ConnectionKey == connectionKey).ToArray();
            if (removed.Length == 0) return Task.CompletedTask;
            foreach (var grant in removed) _grants.Remove(grant.Info.Handle);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _revocations.Add(connectionKey, completion);
        }
        _ = DrainRevokedAsync(connectionKey, removed, completion);
        return completion.Task;
    }

    private async Task DrainRevokedAsync(string connectionKey, Grant[] removed, TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        foreach (var grant in removed)
        {
            try { await grant.Lifetime.CancelAsync().ConfigureAwait(false); }
            catch (Exception ex) { failures.Add(ex); }
            // Every waiter shares this drain, including close/stop racing with a human takeover.
            await grant.Serial.WaitAsync().ConfigureAwait(false);
            grant.Serial.Release();
        }
        lock (_gate)
        {
            _revocations.Remove(connectionKey);
            if (failures.Count == 0) completion.TrySetResult();
            else completion.TrySetException(failures);
        }
    }

    public async Task<AgentResponse> ExecuteAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        AgentResponse result;
        try { result = await ExecuteCoreAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { result = AgentResponse.Error("cancelled", "The request expired or session access was revoked."); }
        catch (NotSupportedException) { result = AgentResponse.Error("unsupported", "This session does not support that operation."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        { result = AgentResponse.Error("read_failed", "The remote data could not be read as bounded text."); }
        catch (Exception)
        { result = AgentResponse.Error("operation_failed", "The operation failed. Check the desktop connection."); }
        // Never log arbitrary caller-supplied strings or file/terminal payloads.
        var auditMethod = IsKnownMethod(request.Method) ? request.Method : "unknown";
        var auditHandle = request.Session is { Length: 48 } handle && handle.All(char.IsAsciiHexDigit) ? handle : null;
        if (OperationCompleted is { } completed)
            foreach (Action<string, string?, string> handler in completed.GetInvocationList())
                try { handler(auditMethod, auditHandle, result.ErrorCode ?? "ok"); }
                catch (Exception) { /* Observers must not turn an accepted PTY write into a lost acknowledgement. */ }
        return result;
    }

    private async Task<AgentResponse> ExecuteCoreAsync(AgentRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Version != 1) return AgentResponse.Error("version_mismatch", "Expected contract version 1.");
        if (request.Method == "sessions.list")
        {
            lock (_gate) return AgentResponse.Ok(_grants.Values
                .Where(g => g.Session.State == ConnectionState.Connected).Select(g => g.Info with { IsActive = g.IsActive?.Invoke() ?? false }).ToArray());
        }
        if (!IsKnownMethod(request.Method))
            return AgentResponse.Error("unknown_method", "Unknown Agent operation.");
        Grant? grant;
        lock (_gate) _grants.TryGetValue(request.Session ?? "", out grant);
        if (grant is null) return AgentResponse.Error("session_unavailable", "The session is not shared or its handle has expired.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, grant.Lifetime.Token);
        ct = linked.Token;
        await grant.Serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfCancelled(grant, ct);
            if (grant.Session.State != ConnectionState.Connected)
                return AgentResponse.Error("session_unavailable", "The session is disconnected.");
            if (request.Method is "terminal.text" or "terminal.key")
                return await ExecuteInputAsync(grant, request, ct).ConfigureAwait(false);
            AgentResponse result;
            switch (request.Method)
            {
                case "sessions.check": result = CheckSession(grant); break;
                case "sessions.info": result = AgentResponse.Ok(grant.Info with { IsActive = grant.IsActive?.Invoke() ?? false }); break;
                case "sessions.activate":
                    if (!grant.Info.TerminalControl) return AgentResponse.Error("control_required", "Allow terminal control in the desktop Agent panel first.");
                    var view = request.View ?? "terminal";
                    if (view is not ("terminal" or "files" or "monitor")) return AgentResponse.Error("invalid_arguments", "View must be terminal, files or monitor.");
                    if (grant.Activate is null) throw new NotSupportedException();
                    await grant.Activate(view, ct).ConfigureAwait(false);
                    result = AgentResponse.Ok(new { activated = true, view });
                    break;
                case "terminal.screen":
                    if (grant.Terminal is not null) { result = AgentResponse.Ok(grant.Terminal.ReadScreen()); break; }
                    if (grant.ReadScreen is null) throw new NotSupportedException();
                    var screen = grant.ReadScreen();
                    result = AgentResponse.Ok(new { source = "screen", text = screen[..Math.Min(screen.Length, 32768)], truncated = screen.Length > 32768 });
                    break;
                case "terminal.output":
                    if (grant.Terminal is null) throw new NotSupportedException();
                    if (request.Cursor is < 0) return AgentResponse.Error("invalid_cursor", "The output cursor must be non-negative.");
                    try { result = AgentResponse.Ok(grant.Terminal.ReadOutput(request.Cursor)); }
                    catch (ArgumentOutOfRangeException) { return AgentResponse.Error("invalid_cursor", "The cursor is ahead of this terminal's output."); }
                    break;
                case "monitor.snapshot":
                    result = AgentResponse.Ok(await (grant.Session.Monitor ?? throw new NotSupportedException()).SampleAsync(ct).ConfigureAwait(false));
                    break;
                default:
                    if (string.IsNullOrWhiteSpace(request.Path) || request.Path.Length > 4096 || request.Path.Any(char.IsControl))
                        return AgentResponse.Error("invalid_path", "Supply a remote path without control characters (maximum 4096 characters).");
                    // Evaluate the host policy for every exact path and operation. The desktop
                    // explicitly allows reads within a shared connection; this is not a path sandbox
                    // or a per-read prompt. Symlinks may resolve outside a lexical root.
                    var approved = await _confirmFile(new AgentFileAccess(grant.Info.Label, request.Method, request.Path), ct).ConfigureAwait(false);
                    ThrowIfCancelled(grant, ct);
                    if (!approved)
                        return AgentResponse.Error("denied", "File access was declined by the host policy.");
                    var files = grant.Session.FileSystem ?? throw new NotSupportedException();
                    if (request.Method == "files.list")
                    {
                        var entries = await files.ListAsync(request.Path, ct).ConfigureAwait(false);
                        result = AgentResponse.Ok(new { entries = entries.Take(500).ToArray(), truncated = entries.Count > 500 });
                    }
                    else
                    {
                        await using var stream = await files.OpenReadAsync(request.Path, ct).ConfigureAwait(false);
                        var text = await BoundedTextFile.ReadAsync(stream, 128 * 1024, ct).ConfigureAwait(false);
                        result = AgentResponse.Ok(new { path = request.Path, text });
                    }
                    break;
            }
            ThrowIfCancelled(grant, ct);
            return result;
        }
        finally { grant.Serial.Release(); }
    }

    private static void ThrowIfCancelled(Grant grant, CancellationToken ct)
    {
        // CancelAsync marks Lifetime immediately, but linked-token callbacks run later.
        // A delayed callback must not let a revoked operation continue or return data.
        grant.Lifetime.Token.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
    }

    private static bool IsKnownMethod(string method) => method is "sessions.list" or "sessions.info" or "sessions.activate" or "sessions.check"
        or "terminal.screen" or "terminal.output" or "terminal.text" or "terminal.key"
        or "monitor.snapshot" or "files.list" or "files.read";

    private sealed class Grant(string connectionKey, AgentSessionInfo info, IProtocolSession session, Func<string>? readScreen,
        ITerminalAutomation? terminal, Func<string, CancellationToken, Task>? activate, Func<bool>? isActive)
    {
        public string ConnectionKey { get; } = connectionKey;
        public AgentSessionInfo Info { get; } = info;
        public IProtocolSession Session { get; } = session;
        public Func<string>? ReadScreen { get; } = readScreen;
        public ITerminalAutomation? Terminal { get; } = terminal;
        public Func<string, CancellationToken, Task>? Activate { get; } = activate;
        public Func<bool>? IsActive { get; } = isActive;
        public Dictionary<string, (string Fingerprint, AgentResponse Response)> Inputs { get; } = new(StringComparer.Ordinal);
        public SessionCheck? Check { get; set; }
        public CancellationTokenSource Lifetime { get; } = new();
        public SemaphoreSlim Serial { get; } = new(1, 1);
    }

    private sealed record SessionCheck(string Token, DateTimeOffset ExpiresUtc, long InputRevision, bool IsActive);

    private AgentResponse CheckSession(Grant grant)
    {
        if (grant.Info.Target is null) return AgentResponse.Error("session_context_unavailable", "The host must supply the connection target before terminal control.");
        if (grant.Terminal is null) throw new NotSupportedException();
        var screen = grant.Terminal.ReadScreen();
        if (screen.IsClosed) return AgentResponse.Error("terminal_unavailable", "The terminal is closed.");
        var active = grant.IsActive?.Invoke() ?? false;
        var expires = _timeProvider.GetUtcNow().AddMinutes(2);
        var check = new SessionCheck(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), expires, screen.InputRevision, active);
        grant.Check = check;
        return AgentResponse.Ok(new
        {
            session = grant.Info with { IsActive = active }, connectionState = grant.Session.State,
            usernameSource = "configured_login", effectiveShellUser = (string?)null,
            screen, checkToken = check.Token, expiresUtc = expires,
            instruction = "Verify server, port, session ID, configured login and current screen against your intended target. Effective user after su, nested SSH or containers is unknown. Use this checkToken and screen.inputRevision for one new terminal input; repeat Check Sessions before each new operation.",
        });
    }
}
