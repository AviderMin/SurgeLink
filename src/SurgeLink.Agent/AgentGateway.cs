using System.Security.Cryptography;
using SurgeLink.Core.Files;
using SurgeLink.Core.Sessions;

namespace SurgeLink.Agent;

/// <summary>
/// Owns opt-in access to existing desktop connections. It never creates connections or reads credentials.
/// A grant is process-local, refers to exactly one connection, and is drained before that connection closes.
/// </summary>
public sealed class AgentGateway
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly Func<AgentFileAccess, CancellationToken, Task<bool>> _confirmFile;

    public AgentGateway(Func<AgentFileAccess, CancellationToken, Task<bool>> confirmFile) => _confirmFile = confirmFile;

    public event Action<string, string?, string>? OperationCompleted;

    public string GrantSession(string connectionKey, string label, IProtocolSession session, Func<string>? readScreen)
    {
        lock (_gate)
        {
            if (session.State != ConnectionState.Connected)
                throw new InvalidOperationException("Only connected sessions can be shared.");
            var existing = _grants.Values.FirstOrDefault(g => g.ConnectionKey == connectionKey);
            if (existing is not null) return existing.Info.Handle;
            var info = new AgentSessionInfo(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), label,
                session.Protocol.Id, session.HostInfo, readScreen is not null,
                session.FileSystem is not null, session.Monitor is not null);
            _grants.Add(info.Handle, new Grant(connectionKey, info, session, readScreen));
            return info.Handle;
        }
    }

    public bool IsShared(string connectionKey)
    {
        lock (_gate) return _grants.Values.Any(g => g.ConnectionKey == connectionKey);
    }

    public async Task RevokeAsync(string connectionKey)
    {
        Grant[] removed;
        lock (_gate)
        {
            removed = _grants.Values.Where(g => g.ConnectionKey == connectionKey).ToArray();
            foreach (var grant in removed) _grants.Remove(grant.Info.Handle);
        }
        foreach (var grant in removed)
        {
            await grant.Lifetime.CancelAsync().ConfigureAwait(false);
            // Wait for an in-flight read before allowing the desktop to dispose the transport.
            await grant.Serial.WaitAsync().ConfigureAwait(false);
            grant.Serial.Release();
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
        var auditMethod = request.Method is "sessions.list" or "sessions.info" or "terminal.screen"
            or "monitor.snapshot" or "files.list" or "files.read" ? request.Method : "unknown";
        var auditHandle = request.Session is { Length: 48 } handle && handle.All(char.IsAsciiHexDigit) ? handle : null;
        OperationCompleted?.Invoke(auditMethod, auditHandle, result.ErrorCode ?? "ok");
        return result;
    }

    private async Task<AgentResponse> ExecuteCoreAsync(AgentRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Version != 1) return AgentResponse.Error("version_mismatch", "Expected contract version 1.");
        if (request.Method == "sessions.list")
        {
            lock (_gate) return AgentResponse.Ok(_grants.Values
                .Where(g => g.Session.State == ConnectionState.Connected).Select(g => g.Info).ToArray());
        }
        if (request.Method is not ("sessions.info" or "terminal.screen" or "monitor.snapshot" or "files.list" or "files.read"))
            return AgentResponse.Error("unknown_method", "Unknown read-only operation.");
        Grant? grant;
        lock (_gate) _grants.TryGetValue(request.Session ?? "", out grant);
        if (grant is null) return AgentResponse.Error("session_unavailable", "The session is not shared or its handle has expired.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, grant.Lifetime.Token);
        ct = linked.Token;
        await grant.Serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (grant.Session.State != ConnectionState.Connected)
                return AgentResponse.Error("session_unavailable", "The session is disconnected.");
            AgentResponse result;
            switch (request.Method)
            {
                case "sessions.info": result = AgentResponse.Ok(grant.Info); break;
                case "terminal.screen":
                    if (grant.ReadScreen is null) throw new NotSupportedException();
                    var screen = grant.ReadScreen();
                    result = AgentResponse.Ok(new { source = "screen", text = screen[..Math.Min(screen.Length, 32768)], truncated = screen.Length > 32768 });
                    break;
                case "monitor.snapshot":
                    result = AgentResponse.Ok(await (grant.Session.Monitor ?? throw new NotSupportedException()).SampleAsync(ct).ConfigureAwait(false));
                    break;
                default:
                    if (string.IsNullOrWhiteSpace(request.Path) || request.Path.Length > 4096 || request.Path.Any(char.IsControl))
                        return AgentResponse.Error("invalid_path", "Supply a remote path without control characters (maximum 4096 characters).");
                    // No claim of path sandboxing: each exact path/operation requires desktop approval.
                    // This also covers directory listings and symlinks, which may resolve outside a lexical root.
                    if (!await _confirmFile(new AgentFileAccess(grant.Info.Label, request.Method, request.Path), ct).ConfigureAwait(false))
                        return AgentResponse.Error("denied", "File access was declined in the desktop.");
                    ct.ThrowIfCancellationRequested();
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
            ct.ThrowIfCancellationRequested();
            return result;
        }
        finally { grant.Serial.Release(); }
    }

    private sealed class Grant(string connectionKey, AgentSessionInfo info, IProtocolSession session, Func<string>? readScreen)
    {
        public string ConnectionKey { get; } = connectionKey;
        public AgentSessionInfo Info { get; } = info;
        public IProtocolSession Session { get; } = session;
        public Func<string>? ReadScreen { get; } = readScreen;
        public CancellationTokenSource Lifetime { get; } = new();
        public SemaphoreSlim Serial { get; } = new(1, 1);
    }
}
