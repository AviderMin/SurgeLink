using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    private readonly AgentAuditLog _audit = new();
    private readonly Dictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _revocations = new(StringComparer.Ordinal);
    private readonly Func<AgentFileAccess, CancellationToken, Task<bool>> _confirmFile;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _controlIdleTimeout;
    private readonly int _inputPermitsPerMinute;

    /// <summary>Default control lease: idle control grants fall back to read-only access.</summary>
    public static readonly TimeSpan DefaultControlIdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Default sustained terminal-input rate per grant.</summary>
    public const int DefaultInputPermitsPerMinute = 60;

    /// <summary>File access is denied unless the host explicitly supplies an authorization policy.</summary>
    public AgentGateway(Func<AgentFileAccess, CancellationToken, Task<bool>>? confirmFile = null,
        TimeProvider? timeProvider = null, TimeSpan? controlIdleTimeout = null,
        int inputPermitsPerMinute = DefaultInputPermitsPerMinute)
    {
        _confirmFile = confirmFile ?? ((_, _) => Task.FromResult(false));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _controlIdleTimeout = controlIdleTimeout ?? DefaultControlIdleTimeout;
        if (_controlIdleTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(controlIdleTimeout), "The control idle timeout cannot be negative.");
        if (inputPermitsPerMinute < 1)
            throw new ArgumentOutOfRangeException(nameof(inputPermitsPerMinute), "At least one input permit per minute is required.");
        _inputPermitsPerMinute = inputPermitsPerMinute;
    }

    public event Action<string, string?, string>? OperationCompleted;

    public string GrantSession(string connectionKey, string label, IProtocolSession session, Func<string>? readScreen,
        ITerminalAutomation? terminal = null, bool allowControl = false,
        Func<string, CancellationToken, Task>? activate = null, Func<bool>? isActive = null, AgentSessionTarget? target = null,
        bool allowFileRead = false)
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
                session.FileSystem is not null, session.Monitor is not null, allowControl && terminal is not null,
                Target: target, FileRead: allowFileRead && session.FileSystem is not null);
            _grants.Add(info.Handle, new Grant(connectionKey, info, session, readScreen, terminal, activate, isActive,
                _timeProvider.GetUtcNow(), new InputRateLimiter(_inputPermitsPerMinute, _timeProvider)));
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
            if (grant is null) return AgentAccessMode.None;
            ExpireControlIfIdle(grant);
            return grant.Info.TerminalControl ? AgentAccessMode.Control
                : grant.Info.FileRead ? AgentAccessMode.FileRead
                : AgentAccessMode.Observe;
        }
    }

    /// <summary>Pauses every grant for this connection while a human types; returns true only when one was running.</summary>
    public bool SuspendForHuman(string connectionKey)
    {
        lock (_gate) return SetHumanOperating(connectionKey, true);
    }

    /// <summary>Clears a human pause without changing the grant or its handle; returns true only on a real change.</summary>
    public bool ResumeFromHuman(string connectionKey)
    {
        lock (_gate) return SetHumanOperating(connectionKey, false);
    }

    public bool IsHumanOperating(string connectionKey)
    {
        lock (_gate) return _grants.Values.Any(g => g.ConnectionKey == connectionKey && g.HumanOperating);
    }

    private bool SetHumanOperating(string connectionKey, bool operating)
    {
        var changed = false;
        foreach (var grant in _grants.Values.Where(g => g.ConnectionKey == connectionKey && g.HumanOperating != operating))
        {
            grant.HumanOperating = operating;
            changed = true;
        }
        return changed;
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

    /// <summary>Returns a newest-first snapshot of structured Agent audit records.</summary>
    public IReadOnlyList<AgentAuditEntry> GetAuditLog() => _audit.GetEntries();

    /// <summary>Exports structured Agent audit records as JSON Lines (JSONL), oldest first.</summary>
    public string ExportAuditLogJsonLines() => _audit.ExportJsonLines();

    public async Task<AgentResponse> ExecuteAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        AgentResponse result;
        try { result = await ExecuteCoreAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (AgentOperationPausedException) { result = HumanOperatingError(); }
        catch (OperationCanceledException) { result = AgentResponse.Error("cancelled", "The request expired or session access was revoked."); }
        catch (NotSupportedException) { result = AgentResponse.Error("unsupported", "This session does not support that operation."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        { result = AgentResponse.Error("read_failed", "The remote data could not be read as bounded text."); }
        catch (Exception)
        { result = AgentResponse.Error("operation_failed", "The operation failed. Check the desktop connection."); }
        // Never log arbitrary caller-supplied strings or file/terminal payloads.
        // Heartbeats are connection metadata, not user activity; do not flood history/logs.
        if (request.Method is "client.heartbeat" or "client.disconnect") return result;
        var auditMethod = IsKnownMethod(request.Method) ? request.Method : "unknown";
        var auditHandle = request.Session is { Length: 48 } handle && handle.All(char.IsAsciiHexDigit) ? handle : null;
        var auditPath = request.Path is { Length: <= 512 } p && !p.Any(char.IsControl) ? p : null;
        var auditOpId = request.OperationId is { Length: <= 128 } op && !op.Any(char.IsControl) ? op : null;
        _audit.Record(new AgentAuditEntry(
            _timeProvider.GetUtcNow(),
            auditMethod,
            auditHandle,
            auditPath,
            auditOpId,
            result.Success,
            result.ErrorCode ?? "ok"));
        if (OperationCompleted is { } completed)
            foreach (Action<string, string?, string> handler in completed.GetInvocationList())
                try { handler(auditMethod, auditHandle, result.ErrorCode ?? "ok"); }
                catch (Exception) { /* Observers must not turn an accepted PTY write into a lost acknowledgement. */ }
        return result;
    }

    private async Task<AgentResponse> ExecuteCoreAsync(AgentRequest request, CancellationToken ct)
    {
        var requestClock = Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();
        if (request.Version != 1) return AgentResponse.Error("version_mismatch", "Expected contract version 1.");
        if (request.Method is "client.heartbeat" or "client.disconnect") return UpdateMcpPresence(request);
        if (request.Method == "sessions.list")
        {
            lock (_gate)
            {
                foreach (var shared in _grants.Values) ExpireControlIfIdle(shared);
                return AgentResponse.Ok(_grants.Values
                    .Where(g => g.Session.State == ConnectionState.Connected)
                    .Select(g => g.Info with { IsActive = g.IsActive?.Invoke() ?? false, UserOperating = g.HumanOperating }).ToArray());
            }
        }
        if (!IsKnownMethod(request.Method))
            return AgentResponse.Error("unknown_method", "Unknown Agent operation.");
        Grant? grant;
        bool controlExpired;
        lock (_gate)
        {
            _grants.TryGetValue(request.Session ?? "", out grant);
            controlExpired = grant is not null && ExpireControlIfIdle(grant);
        }
        if (grant is null) return AgentResponse.Error("session_unavailable", "The session is not shared or its handle has expired.");
        if (controlExpired && IsControlOperation(request.Method))
            return AgentResponse.Error("control_expired", "Terminal control expired after inactivity. Allow control again in the desktop Agent panel.");
        // A human owns the terminal now: fail fast before any IO or queueing behind Agent work.
        if (IsHumanOperating(grant)) return HumanOperatingError();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, grant.Lifetime.Token);
        ct = linked.Token;
        await grant.Serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Input may have arrived while this request waited; never execute IO after the pause.
            if (IsHumanOperating(grant)) return HumanOperatingError();
            ThrowIfCancelled(grant, ct);
            if (grant.Session.State != ConnectionState.Connected)
                return AgentResponse.Error("session_unavailable", "The session is disconnected.");
            if (CheckControlAfterQueue(grant, request) is { } expiredAfterQueue) return expiredAfterQueue;
            if (request.Method is "terminal.text" or "terminal.key" or "terminal.exec")
                return await ExecuteInputAsync(grant, request, requestClock, ct).ConfigureAwait(false);
            AgentResponse result;
            switch (request.Method)
            {
                case "sessions.check": result = CheckSession(grant, request.Brief, request.Minimal); break;
                case "sessions.info": result = AgentResponse.Ok(grant.Info with { IsActive = grant.IsActive?.Invoke() ?? false }); break;
                case "sessions.activate":
                    if (!grant.Info.TerminalControl) return AgentResponse.Error("control_required", "Allow terminal control in the desktop Agent panel first.");
                    var view = request.View ?? "terminal";
                    if (view is not ("terminal" or "files" or "monitor")) return AgentResponse.Error("invalid_arguments", "View must be terminal, files or monitor.");
                    if (grant.Activate is null) throw new NotSupportedException();
                    await grant.Activate(view, ct).ConfigureAwait(false);
                    result = AgentResponse.Ok(new { activated = true, view });
                    break;
                case "terminal.screen": result = ReadScreenResponse(grant, request); break;
                case "terminal.output": result = ReadOutputResponse(grant, request); break;
                case "terminal.wait":
                    (result, _) = await WaitOutputAsync(grant, request, ct,
                        maxTimeoutMilliseconds: RemainingWaitMilliseconds(requestClock)).ConfigureAwait(false);
                    break;
                case "monitor.snapshot":
                    result = AgentResponse.Ok(await (grant.Session.Monitor ?? throw new NotSupportedException()).SampleAsync(ct).ConfigureAwait(false));
                    break;
                default:
                    if (string.IsNullOrWhiteSpace(request.Path) || request.Path.Length > 4096 || request.Path.Any(char.IsControl))
                        return AgentResponse.Error("invalid_path", "Supply a remote path without control characters (maximum 4096 characters).");
                    if (request.Method == "files.rename"
                        && (string.IsNullOrWhiteSpace(request.Destination) || request.Destination.Length > 4096 || request.Destination.Any(char.IsControl)))
                        return AgentResponse.Error("invalid_path", "Supply a destination path without control characters (maximum 4096 characters).");
                    // Check the grant before showing a host confirmation. A user must
                    // never approve an upload/download that the gateway will reject.
                    if (CheckFilePermission(grant, request.Method) is { } permissionFailure)
                        return permissionFailure;
                    if (request.Method is "files.upload" or "files.download")
                    {
                        if (TryNormalizeLocalPath(request.LocalPath, request.Method == "files.upload", out var normalizedLocalPath, out var localPathError))
                            request = request with { LocalPath = normalizedLocalPath };
                        else
                            return localPathError!;
                    }
                    if (IsFileMutation(request.Method) && !grant.InputRate.TryAcquire())
                        return AgentResponse.Error("input_rate_limited", "Too many control operations in a short period. Wait before retrying.");
                    // Evaluate the host policy for every exact path and operation. The desktop
                    // explicitly allows reads within a shared connection; this is not a path sandbox
                    // or a per-read prompt. Local upload/download paths are included so the host
                    // can make the Agent's local file access visible to the user.
                    var approved = await _confirmFile(
                        new AgentFileAccess(grant.Info.Label, request.Method, request.Path,
                            request.LocalPath, request.Destination)
                        {
                            Target = grant.Info.Target,
                            Overwrite = request.Overwrite,
                        }, ct).ConfigureAwait(false);
                    ThrowIfCancelled(grant, ct);
                    // The host may hold this request while a human decides. If the user takes
                    // the terminal in that window, the approval must not run behind their back:
                    // re-check the pause, control expiry and permission immediately before IO.
                    if (IsHumanOperating(grant)) return HumanOperatingError();
                    if (CheckControlAfterQueue(grant, request) is { } expiredAfterApproval) return expiredAfterApproval;
                    if (CheckFilePermission(grant, request.Method) is { } postApprovalFailure)
                        return postApprovalFailure;
                    if (!approved)
                        return AgentResponse.Error("denied", "File access was declined by the host policy.");
                    result = await ExecuteFileAsync(grant, request, ct).ConfigureAwait(false);
                    break;
            }
            ThrowIfCancelled(grant, ct);
            // A control operation renews the lease only after it was authorized and
            // completed; a denied, expired or failed request must not keep control alive.
            if (result.Success && IsControlOperation(request.Method)) TouchControl(grant);
            return result;
        }
        finally { grant.Serial.Release(); }
    }

    private static AgentResponse ReadScreenResponse(Grant grant, AgentRequest request)
    {
        var lineLimit = request.TailRows ?? request.MaxLines;
        if (grant.Terminal is not null)
        {
            var screen = grant.Terminal.ReadScreen();
            var trimmed = AgentText.TrimTail(screen.Text, request.StripAnsi, request.MaxBytes, lineLimit);
            return AgentResponse.Ok(screen with
            {
                Text = trimmed.Text,
                Truncated = screen.Truncated || trimmed.Truncated,
            });
        }
        if (grant.ReadScreen is null) throw new NotSupportedException();
        var raw = grant.ReadScreen();
        var count = Math.Min(raw.Length, 32768);
        if (count > 0 && char.IsHighSurrogate(raw[count - 1])) count--;
        var visible = AgentText.TrimTail(raw[..count], request.StripAnsi, request.MaxBytes, lineLimit);
        return AgentResponse.Ok(new
        {
            source = "screen", text = visible.Text,
            truncated = count < raw.Length || visible.Truncated,
            originalCharacters = raw.Length,
        });
    }

    private static AgentResponse ReadOutputResponse(Grant grant, AgentRequest request)
    {
        if (grant.Terminal is null) throw new NotSupportedException();
        if (request.Cursor is < 0) return AgentResponse.Error("invalid_cursor", "The output cursor must be non-negative.");
        try
        {
            var page = grant.Terminal.ReadOutput(request.Cursor);
            var trimmed = AgentText.TrimHead(page.Text, request.StripAnsi, request.MaxBytes, request.MaxLines);
            return AgentResponse.Ok(new
            {
                text = trimmed.Text,
                // The cursor always points just after the characters actually
                // returned, so the next page neither repeats nor skips output.
                cursor = page.Cursor - page.Text.Length + trimmed.KeptCharacters,
                oldestCursor = page.OldestCursor,
                latestCursor = page.LatestCursor,
                gap = page.Gap,
                hasMore = page.HasMore || trimmed.Truncated,
                truncated = trimmed.Truncated,
                originalCharacters = page.Text.Length,
                returnedCharacters = trimmed.Text.Length,
            });
        }
        catch (ArgumentOutOfRangeException)
        {
            return AgentResponse.Error("invalid_cursor", "The cursor is ahead of this terminal's output.");
        }
    }

    private static int RemainingWaitMilliseconds(Stopwatch requestClock) =>
        (int)Math.Clamp(50_000 - requestClock.ElapsedMilliseconds, 100, 50_000);

    private async Task<(AgentResponse Response, int? ExitCode)> WaitOutputAsync(
        Grant grant, AgentRequest request, CancellationToken ct, string? expectText = null,
        int maxTimeoutMilliseconds = 50_000)
    {
        if (grant.Terminal is null) throw new NotSupportedException();
        if (request.Cursor is < 0) return (AgentResponse.Error("invalid_cursor", "The output cursor must be non-negative."), null);
        var idleMilliseconds = Math.Clamp(request.IdleMilliseconds ?? 500, 50, 5000);
        var timeoutMilliseconds = Math.Clamp(request.TimeoutMilliseconds ?? 30000, 100, Math.Clamp(maxTimeoutMilliseconds, 100, 50_000));
        var fromCursor = request.Cursor ?? grant.Terminal.ReadScreen().OutputCursor;
        var cursor = fromCursor;
        var pageStart = fromCursor;
        var text = new StringBuilder();
        long oldestCursor = cursor;
        long latestCursor = cursor;
        var gap = false;
        var hasMore = false;
        var capped = false;
        int? exitCode = null;
        var total = Stopwatch.StartNew();
        var sinceOutput = Stopwatch.StartNew();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (IsHumanOperating(grant)) return (HumanOperatingError(), null);
            TerminalOutputPage page;
            try { page = grant.Terminal.ReadOutput(cursor); }
            catch (ArgumentOutOfRangeException) { return (AgentResponse.Error("invalid_cursor", "The cursor is ahead of this terminal's output."), null); }
            oldestCursor = page.OldestCursor;
            latestCursor = page.LatestCursor;
            gap |= page.Gap;
            if (page.Text.Length > 0)
            {
                if (page.Gap && text.Length > 0)
                {
                    // The retained history skipped a range. Keep the first
                    // contiguous segment; the next call continues from its end.
                    hasMore = true;
                    break;
                }
                if (text.Length == 0) pageStart = page.Cursor - page.Text.Length;
                // An Int32 status can use 11 characters, followed by CRLF.
                var scanStart = Math.Max(0, text.Length - (expectText?.Length ?? 0) - 13);
                text.Append(page.Text);
                cursor = page.Cursor;
                hasMore = page.HasMore;
                sinceOutput.Restart();
                if (expectText is not null
                    && FindExecMarker(text.ToString(scanStart, text.Length - scanStart), expectText,
                        scanStart == 0 || text[scanStart - 1] == '\n') is { } completion)
                {
                    exitCode = completion.ExitCode;
                    text.Remove(scanStart + completion.Start, completion.Length);
                    break;
                }
                if (text.Length > 512 * 1024) { capped = true; break; }
                if (page.HasMore) continue;
            }
            // terminal.exec waits for its sentinel: a quiet gap in a long-running
            // command's output is not completion. Only incremental waits settle on idle.
            if (expectText is null && text.Length > 0 && sinceOutput.ElapsedMilliseconds >= idleMilliseconds) break;
            if (total.ElapsedMilliseconds >= timeoutMilliseconds) break;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        // For an exec wait the marker is the only completion signal, so reaching the
        // deadline without it is a timeout no matter how recently output arrived.
        var timedOut = exitCode is null && total.ElapsedMilliseconds >= timeoutMilliseconds
            && (expectText is not null || text.Length == 0 || sinceOutput.ElapsedMilliseconds < idleMilliseconds);
        var rawText = text.ToString();
        // A command result keeps the final output. Incremental waits keep the head and report the cursor just after
        // the returned characters, which makes repeated pages lossless. Trimming
        // accepts ANSI-aware limits so a page never ends inside an escape.
        var trimmed = expectText is not null
            ? AgentText.TrimTail(rawText, request.StripAnsi, request.MaxBytes ?? 65536, request.MaxLines)
            : AgentText.TrimHead(rawText, request.StripAnsi, request.MaxBytes ?? 65536, request.MaxLines);
        var visible = trimmed.Text;
        // Only the segment that starts at the wait's original cursor can begin
        // with the just-sent echo; a later page must not lose real output that
        // happens to repeat the command text near its start.
        if (grant.LastInputText is { } echoText && (expectText is not null || pageStart == fromCursor))
            visible = AgentText.StripLeadingEcho(visible, echoText);
        var nextCursor = expectText is not null ? cursor : pageStart + trimmed.KeptCharacters;
        var screen = grant.Terminal.ReadScreen();
        var screenTail = AgentText.TrimTail(screen.Text, request.StripAnsi, 4096, 4);
        return (AgentResponse.Ok(new
        {
            text = visible,
            cursor = nextCursor,
            oldestCursor,
            latestCursor,
            gap,
            hasMore = hasMore || capped || expectText is null && trimmed.Truncated,
            truncated = trimmed.Truncated || capped,
            idle = !timedOut && !capped,
            timedOut,
            elapsedMilliseconds = total.ElapsedMilliseconds,
            commandCompletionKnown = false,
            screen = screen with
            {
                Text = screenTail.Text,
                Truncated = screen.Truncated || screenTail.Truncated,
            },
        }), exitCode);
    }

    private static (int Start, int Length, int ExitCode)? FindExecMarker(string text, string marker, bool startsAtLineBoundary)
    {
        var index = 0;
        while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            if (index == 0 ? !startsAtLineBoundary : text[index - 1] != '\n')
            {
                index += marker.Length;
                continue;
            }
            var cursor = index + marker.Length;
            if (cursor < text.Length && text[cursor] == '-') cursor++;
            var digitsStart = cursor;
            while (cursor < text.Length && char.IsAsciiDigit(text[cursor])) cursor++;
            if (cursor > digitsStart)
            {
                if (cursor < text.Length && text[cursor] == '\r') cursor++;
                if (cursor < text.Length && text[cursor] == '\n'
                    && int.TryParse(text.AsSpan(index + marker.Length, cursor - index - marker.Length).TrimEnd(),
                        System.Globalization.NumberStyles.AllowLeadingSign,
                        System.Globalization.CultureInfo.InvariantCulture, out var code))
                    return (index, cursor + 1 - index, code);
            }
            index += marker.Length;
        }
        return null;
    }

    private async Task<AgentResponse> ExecuteFileAsync(Grant grant, AgentRequest request, CancellationToken ct)
    {
        if (CheckControlAfterQueue(grant, request) is { } expired) return expired;
        if (CheckFilePermission(grant, request.Method) is { } permissionFailure) return permissionFailure;
        var files = grant.Session.FileSystem ?? throw new NotSupportedException();
        switch (request.Method)
        {
            case "files.list":
            {
                var entries = await files.ListAsync(request.Path!, ct).ConfigureAwait(false);
                var offset = request.Offset ?? 0;
                if (offset < 0) return AgentResponse.Error("invalid_arguments", "The listing offset must be non-negative.");
                var limit = Math.Clamp(request.Limit ?? 500, 1, 500);
                var start = offset > int.MaxValue ? int.MaxValue : (int)offset;
                var page = entries.Skip(start).Take(limit).ToArray();
                return AgentResponse.Ok(new
                {
                    entries = page, total = entries.Count, offset, limit,
                    truncated = start + page.Length < entries.Count,
                });
            }
            case "files.read":
                return await ReadFileAsync(files, request, ct).ConfigureAwait(false);
            case "files.stat":
            {
                var entry = await files.TryStatAsync(request.Path!, ct).ConfigureAwait(false);
                return AgentResponse.Ok(new { exists = entry is not null, entry });
            }
            case "files.write":
            {
                if (request.Text is null) return AgentResponse.Error("invalid_arguments", "Supply text to write.");
                if (!request.Path!.StartsWith('/')) return AgentResponse.Error("invalid_path", "A remote write path must be absolute.");
                var bytes = new UTF8Encoding(false, true).GetBytes(request.Text);
                try
                {
                    await RemoteFileWrite.WriteAsync(files, request.Path, bytes.Length, null, request.Overwrite,
                        async (stream, token) => await stream.WriteAsync(bytes, token).ConfigureAwait(false), ct,
                        validateDestination: token => CheckTransferControlAsync(grant, token)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return AgentResponse.Error("write_failed", ex.Message);
                }
                return AgentResponse.Ok(new { path = request.Path, bytesWritten = bytes.Length, overwrite = request.Overwrite });
            }
            case "files.delete":
                if (request.Recursive && IsRemoteRoot(request.Path!))
                    return AgentResponse.Error("invalid_path", "Refusing to delete the remote root directory recursively.");
                await files.DeleteAsync(request.Path!, request.Recursive, ct).ConfigureAwait(false);
                return AgentResponse.Ok(new { path = request.Path, deleted = true, recursive = request.Recursive });
            case "files.rename":
                await files.RenameAsync(request.Path!, request.Destination!, ct).ConfigureAwait(false);
                return AgentResponse.Ok(new { path = request.Path, destination = request.Destination, moved = true });
            case "files.mkdir":
                await files.CreateDirectoryAsync(request.Path!, ct).ConfigureAwait(false);
                return AgentResponse.Ok(new { path = request.Path, created = true });
            case "files.upload":
                return await UploadFileAsync(grant, files, request, ct).ConfigureAwait(false);
            case "files.download":
                return await DownloadFileAsync(grant, files, request, ct).ConfigureAwait(false);
            default:
                throw new NotSupportedException();
        }
    }

    private static bool IsUnsafePathChar(char c) =>
        char.IsControl(c)
        || c is >= '\u202A' and <= '\u202E'
        || c is >= '\u2066' and <= '\u2069'
        || c is '\u200E' or '\u200F' or '\u061C';

    private static bool TryNormalizeLocalPath(string? rawPath, bool upload, out string normalized, out AgentResponse? error)
    {
        normalized = string.Empty;
        var verb = upload ? "upload" : "download";
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Length > 4096 || rawPath.Any(IsUnsafePathChar))
        {
            error = AgentResponse.Error("invalid_path", $"Supply a local {verb} path without control or bidirectional override characters.");
            return false;
        }
        var trimmed = rawPath.TrimStart();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            error = AgentResponse.Error("invalid_path", $"Network UNC and device paths are not allowed for local {verb} operations.");
            return false;
        }
        string fullPath;
        try { fullPath = Path.GetFullPath(rawPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = AgentResponse.Error("invalid_path", $"The local {verb} path is invalid.");
            return false;
        }
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (string.IsNullOrEmpty(root)
            || root.StartsWith(@"\\", StringComparison.Ordinal)
            || fullPath[root.Length..].Contains(':'))
        {
            error = AgentResponse.Error("invalid_path", $"The local {verb} path must be a standard local file path without alternate data streams.");
            return false;
        }
        var directory = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
        {
            error = AgentResponse.Error("invalid_path", $"The local {verb} path must name a file inside a directory.");
            return false;
        }
        try { _ = SurgeLink.Core.Files.LocalDownloadPath.Resolve(directory, fileName); }
        catch (IOException)
        {
            error = AgentResponse.Error("invalid_path", $"The local {verb} file name is invalid or reserved by Windows.");
            return false;
        }
        normalized = fullPath;
        error = null;
        return true;
    }

    private async Task<AgentResponse> UploadFileAsync(Grant grant, IRemoteFileSystem files, AgentRequest request, CancellationToken ct)
    {
        if (!TryNormalizeLocalPath(request.LocalPath, upload: true, out var localPath, out var pathError))
            return pathError!;
        if (!request.Path!.StartsWith('/')) return AgentResponse.Error("invalid_path", "A remote upload path must be absolute.");
        FileStream localFile;
        try
        {
            localFile = LocalDownloadPath.OpenExistingFileForRead(localPath, rejectHardLinks: true);
        }
        catch (UnsafeLocalPathException ex)
        {
            return AgentResponse.Error("invalid_path", ex.Message);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return AgentResponse.Error("local_file_not_found", "The local upload file does not exist.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AgentResponse.Error("write_failed", ex.Message);
        }
        try
        {
            await using var source = localFile;
            var length = source.Length;
            await RemoteFileWrite.WriteAsync(files, request.Path, length, null, request.Overwrite,
                async (stream, token) =>
                {
                    source.Position = 0;
                    await source.CopyToAsync(stream, 81920, token).ConfigureAwait(false);
                }, ct, validateDestination: token => CheckTransferControlAsync(grant, token)).ConfigureAwait(false);
            return AgentResponse.Ok(new { path = request.Path, localPath, bytesWritten = length, overwrite = request.Overwrite });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AgentResponse.Error("write_failed", ex.Message);
        }
    }

    private async Task<AgentResponse> DownloadFileAsync(Grant grant, IRemoteFileSystem files, AgentRequest request, CancellationToken ct)
    {
        if (!TryNormalizeLocalPath(request.LocalPath, upload: false, out var localPath, out var pathError))
            return pathError!;
        var directory = Path.GetDirectoryName(localPath);
        if (string.IsNullOrEmpty(directory))
            return AgentResponse.Error("invalid_path", "The local download path has no directory.");
        IDisposable directoryLease;
        try
        {
            directoryLease = SurgeLink.Core.Files.LocalDownloadPath.LockDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AgentResponse.Error("invalid_path", ex.Message);
        }
        using (directoryLease)
        {
            // Stage beside the destination and publish only after the remote read
            // finished. Truncating or creating the destination up front would turn a
            // failed or cancelled transfer into data loss.
            var temporary = Path.Combine(directory, ".surge-" + Guid.NewGuid().ToString("N") + ".tmp");
            long written = 0;
            var published = false;
            try
            {
                if (LocalDownloadPath.IsSafeExistingFile(localPath) && !request.Overwrite)
                    return AgentResponse.Error("local_file_exists", "The local download path already exists. Set overwrite=true to allow a numbered sibling name.");
                // Close the remote source before publishing the destination. A failed
                // close must not publish a file behind a read_failed response.
                await using (var source = await files.OpenReadAsync(request.Path!, ct).ConfigureAwait(false))
                {
                    await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 81920, FileOptions.Asynchronous))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await CheckTransferControlAsync(grant, ct).ConfigureAwait(false);
                            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            written += read;
                        }
                        await target.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
                await CheckTransferControlAsync(grant, ct).ConfigureAwait(false);
                var actualPath = LocalFileCommit.Publish(temporary, localPath, request.Overwrite, ct);
                published = true;
                return AgentResponse.Ok(new { path = request.Path, localPath = actualPath, bytesWritten = written, overwrite = false });
            }
            catch (LocalFileAlreadyExistsException)
            {
                return AgentResponse.Error("local_file_exists", "The local download path already exists. Set overwrite=true to allow a numbered sibling name.");
            }
            catch (UnsafeLocalPathException ex)
            {
                return AgentResponse.Error("invalid_path", ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return AgentResponse.Error("read_failed", ex.Message);
            }
            finally
            {
                if (!published)
                {
                    try { File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }

    private static async Task<AgentResponse> ReadFileAsync(IRemoteFileSystem files, AgentRequest request, CancellationToken ct)
    {
        if (request.TailBytes is not null)
        {
            if (request.Offset is not null)
                return AgentResponse.Error("invalid_arguments", "Specify either offset or tailBytes, not both.");
            if (request.TailBytes <= 0)
                return AgentResponse.Error("invalid_arguments", "tailBytes must be a positive integer.");
            var tailLimit = Math.Clamp(request.TailBytes.Value, 1, 512 * 1024);
            await using var tailStream = await files.OpenReadAsync(request.Path!, ct).ConfigureAwait(false);
            var tail = await AgentFilePage.ReadTailAsync(tailStream, tailLimit, ct).ConfigureAwait(false);
            var tailRange = AgentFilePage.FitWireText(tail.Text, keepTail: true);
            var removed = System.Text.Encoding.UTF8.GetByteCount(tail.Text.AsSpan(0, tailRange.Start));
            var tailOffset = removed == 0 ? tail.Offset : tail.Offset + tail.SkippedBytes + removed;
            var tailBytes = tail.Offset + tail.BytesRead - tailOffset;
            return AgentResponse.Ok(new
            {
                path = request.Path,
                text = StripLeadingBom(tail.Text[tailRange.Start..], tailOffset),
                offset = tailOffset,
                bytesRead = tailBytes,
                skippedBytes = removed == 0 ? tail.SkippedBytes : 0,
                truncated = tail.Truncated || removed != 0,
                nextOffset = tail.Offset + tail.BytesRead,
                tail = true,
            });
        }

        if (request.Offset is < 0) return AgentResponse.Error("invalid_arguments", "The file offset must be non-negative.");
        await using var stream = await files.OpenReadAsync(request.Path!, ct).ConfigureAwait(false);
        if (request.MaxBytes is null && request.Offset is null)
        {
            var legacyText = await BoundedTextFile.ReadAsync(stream, 128 * 1024, ct).ConfigureAwait(false);
            return AgentResponse.Ok(new { path = request.Path, text = legacyText });
        }

        var offset = request.Offset ?? 0;
        var limit = Math.Clamp(request.MaxBytes ?? 128 * 1024, 1, 512 * 1024);
        var page = await AgentFilePage.ReadAsync(stream, offset, limit, ct).ConfigureAwait(false);
        var pageRange = AgentFilePage.FitWireText(page.Text, keepTail: false);
        var bytesRead = page.SkippedBytes + System.Text.Encoding.UTF8.GetByteCount(page.Text.AsSpan(0, pageRange.Length));
        return AgentResponse.Ok(new
        {
            path = request.Path, text = StripLeadingBom(page.Text[..pageRange.Length], offset), offset,
            // Pages end on a UTF-8 character boundary. A request whose offset falls
            // inside a character is aligned forward and reports the skipped bytes.
            bytesRead,
            skippedBytes = page.SkippedBytes,
            truncated = page.Truncated || bytesRead != page.BytesRead,
            nextOffset = offset + bytesRead,
        });
    }

    private static string StripLeadingBom(string text, long offset) =>
        offset == 0 && text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;

    private static void ThrowIfCancelled(Grant grant, CancellationToken ct)
    {
        // CancelAsync marks Lifetime immediately, but linked-token callbacks run later.
        // A delayed callback must not let a revoked operation continue or return data.
        grant.Lifetime.Token.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>True when every segment is a dot segment, i.e. the path names the POSIX root.</summary>
    private static bool IsRemoteRoot(string path)
    {
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is not ("." or "..")) return false;
        }
        return true;
    }

    private bool IsHumanOperating(Grant grant)
    {
        lock (_gate) return grant.HumanOperating;
    }

    private static AgentResponse HumanOperatingError() => AgentResponse.Error("user_operating",
        "User is operating this terminal. Stop all current operations and wait for the user to return control; do not retry or re-acquire the session.");

    private static bool IsKnownMethod(string method) => method is "sessions.list" or "sessions.info" or "sessions.activate" or "sessions.check"
        or "terminal.screen" or "terminal.output" or "terminal.wait" or "terminal.text" or "terminal.exec" or "terminal.key"
        or "monitor.snapshot" or "files.list" or "files.read" or "files.stat" or "files.write"
        or "files.delete" or "files.rename" or "files.mkdir" or "files.upload" or "files.download";

    private static bool IsControlOperation(string method) => method is "terminal.text" or "terminal.key" or "terminal.exec"
        or "sessions.activate" || IsFileMutation(method);

    private static bool IsFileMutation(string method) => method is
        "files.write" or "files.delete" or "files.rename" or "files.mkdir" or "files.upload" or "files.download";

    private Task CheckTransferControlAsync(Grant grant, CancellationToken cancellationToken)
    {
        ThrowIfCancelled(grant, cancellationToken);
        if (IsHumanOperating(grant)) throw new AgentOperationPausedException();
        return Task.CompletedTask;
    }

    private bool ExpireControlIfIdle(Grant grant)
    {
        if (!grant.Info.TerminalControl) return false;
        if (_controlIdleTimeout == TimeSpan.Zero
            || _timeProvider.GetUtcNow() - grant.LastControlActivityUtc >= _controlIdleTimeout)
        {
            grant.Info = grant.Info with { TerminalControl = false };
            // Human pause is independent of the control grant. Clearing it here
            // would silently resume reads even though the user is still operating.
            return true;
        }
        return false;
    }

    private AgentResponse? CheckControlAfterQueue(Grant grant, AgentRequest request)
    {
        bool expired;
        lock (_gate) expired = ExpireControlIfIdle(grant);
        return expired && IsControlOperation(request.Method)
            ? AgentResponse.Error("control_expired",
                "Terminal control expired after inactivity. Allow control again in the desktop Agent panel.")
            : null;
    }

    private static AgentResponse? CheckFilePermission(Grant grant, string method)
    {
        if ((method is "files.list" or "files.read" or "files.stat") && !grant.Info.FileRead)
            return AgentResponse.Error("file_access_required", "Allow file access in the desktop Agent panel first.");
        if (IsFileMutation(method))
        {
            if (!grant.Info.TerminalControl)
                return AgentResponse.Error("control_required", "Allow terminal control in the desktop Agent panel before changing files.");
            if (grant.InputOutcomeUnknown)
                return AgentResponse.Error("input_outcome_unknown",
                    "A previous PTY write may be partial. Inspect the terminal and renew terminal control before changing files.");
        }
        return null;
    }

    private void TouchControl(Grant grant)
    {
        lock (_gate)
        {
            if (grant.Info.TerminalControl)
                grant.LastControlActivityUtc = _timeProvider.GetUtcNow();
        }
    }

    private sealed class Grant(string connectionKey, AgentSessionInfo info, IProtocolSession session, Func<string>? readScreen,
        ITerminalAutomation? terminal, Func<string, CancellationToken, Task>? activate, Func<bool>? isActive,
        DateTimeOffset grantedAt, InputRateLimiter inputRate)
    {
        public string ConnectionKey { get; } = connectionKey;
        public AgentSessionInfo Info { get; set; } = info;
        public IProtocolSession Session { get; } = session;
        public Func<string>? ReadScreen { get; } = readScreen;
        public ITerminalAutomation? Terminal { get; } = terminal;
        public Func<string, CancellationToken, Task>? Activate { get; } = activate;
        public Func<bool>? IsActive { get; } = isActive;
        public bool HumanOperating { get; set; }
        public string? LastInputText { get; set; }
        public DateTimeOffset LastControlActivityUtc { get; set; } = grantedAt;
        public InputRateLimiter InputRate { get; } = inputRate;
        public InputReplayCache Inputs { get; } = new();
        public SessionCheck? Check { get; set; }
        public bool InputOutcomeUnknown { get; set; }
        public CancellationTokenSource Lifetime { get; } = new();
        public SemaphoreSlim Serial { get; } = new(1, 1);
    }

    private sealed record SessionCheck(string Token, DateTimeOffset ExpiresUtc, TerminalScreenSnapshot Screen, bool IsActive);

    private AgentResponse CheckSession(Grant grant, bool brief, bool minimal)
    {
        if (grant.Info.Target is null) return AgentResponse.Error("session_context_unavailable", "The host must supply the connection target before terminal control.");
        if (grant.Terminal is null) throw new NotSupportedException();
        var screen = grant.Terminal.ReadScreen();
        if (screen.IsClosed) return AgentResponse.Error("terminal_unavailable", "The terminal is closed.");
        var active = grant.IsActive?.Invoke() ?? false;
        var expires = _timeProvider.GetUtcNow().AddMinutes(2);
        var check = new SessionCheck(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), expires, screen, active);
        grant.Check = check;
        var compact = brief || minimal;
        object sessionResult = minimal
            ? new
            {
                handle = grant.Info.Handle, label = grant.Info.Label, target = grant.Info.Target,
                terminalControl = grant.Info.TerminalControl, fileRead = grant.Info.FileRead,
                isActive = active, userOperating = IsHumanOperating(grant),
            }
            : grant.Info with { IsActive = active };
        object screenResult = screen;
        if (compact)
        {
            var tail = AgentText.TrimTail(screen.Text, stripAnsi: false,
                maxBytes: minimal ? 2048 : 4096, maxLines: minimal ? 3 : 8);
            screenResult = new
            {
                text = tail.Text,
                truncated = screen.Truncated || tail.Truncated,
                screen.Columns, screen.Rows, screen.CursorRow, screen.CursorColumn, screen.CursorVisible,
                screen.AlternateScreen, screen.BracketedPaste, screen.MouseTracking, screen.InputRevision,
                screen.OutputCursor, screen.IsClosed, screen.ViewportOffset, screen.Source,
            };
        }
        return AgentResponse.Ok(new
        {
            session = sessionResult, connectionState = grant.Session.State,
            usernameSource = "configured_login", effectiveShellUser = (string?)null,
            screen = screenResult, checkToken = check.Token, expiresUtc = expires,
            brief = compact, minimal,
            instruction = "Verify server, port, session ID, configured login and current screen against your intended target. Effective user after su, nested SSH or containers is unknown. Use this checkToken and screen.inputRevision for one new terminal input; repeat Check Sessions before each new operation.",
        });
    }
}
