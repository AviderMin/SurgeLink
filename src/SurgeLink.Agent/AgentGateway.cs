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
        if (request.Method is "client.heartbeat" or "client.disconnect") return UpdateMcpPresence(request);
        if (request.Method == "sessions.list")
        {
            lock (_gate) return AgentResponse.Ok(_grants.Values
                .Where(g => g.Session.State == ConnectionState.Connected)
                .Select(g => g.Info with { IsActive = g.IsActive?.Invoke() ?? false, UserOperating = g.HumanOperating }).ToArray());
        }
        if (!IsKnownMethod(request.Method))
            return AgentResponse.Error("unknown_method", "Unknown Agent operation.");
        Grant? grant;
        lock (_gate) _grants.TryGetValue(request.Session ?? "", out grant);
        if (grant is null) return AgentResponse.Error("session_unavailable", "The session is not shared or its handle has expired.");
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
            if (request.Method is "terminal.text" or "terminal.key" or "terminal.exec")
                return await ExecuteInputAsync(grant, request, ct).ConfigureAwait(false);
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
                case "terminal.wait": result = await WaitOutputAsync(grant, request, ct).ConfigureAwait(false); break;
                case "monitor.snapshot":
                    result = AgentResponse.Ok(await (grant.Session.Monitor ?? throw new NotSupportedException()).SampleAsync(ct).ConfigureAwait(false));
                    break;
                default:
                    if (string.IsNullOrWhiteSpace(request.Path) || request.Path.Length > 4096 || request.Path.Any(char.IsControl))
                        return AgentResponse.Error("invalid_path", "Supply a remote path without control characters (maximum 4096 characters).");
                    if (request.Method == "files.rename"
                        && (string.IsNullOrWhiteSpace(request.Destination) || request.Destination.Length > 4096 || request.Destination.Any(char.IsControl)))
                        return AgentResponse.Error("invalid_path", "Supply a destination path without control characters (maximum 4096 characters).");
                    // Evaluate the host policy for every exact path and operation. The desktop
                    // explicitly allows reads within a shared connection; this is not a path sandbox
                    // or a per-read prompt. Local upload/download paths are included so the host
                    // can make the Agent's local file access visible to the user.
                    var approved = await _confirmFile(
                        new AgentFileAccess(grant.Info.Label, request.Method, request.Path,
                            request.LocalPath, request.Destination), ct).ConfigureAwait(false);
                    ThrowIfCancelled(grant, ct);
                    if (!approved)
                        return AgentResponse.Error("denied", "File access was declined by the host policy.");
                    result = await ExecuteFileAsync(grant, request, ct).ConfigureAwait(false);
                    break;
            }
            ThrowIfCancelled(grant, ct);
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
            var trimmed = AgentText.Trim(screen.Text, request.StripAnsi, request.MaxBytes, lineLimit);
            return AgentResponse.Ok(screen with
            {
                Text = trimmed.Text,
                Truncated = screen.Truncated || trimmed.DroppedCharacters > 0,
            });
        }
        if (grant.ReadScreen is null) throw new NotSupportedException();
        var raw = grant.ReadScreen();
        var count = Math.Min(raw.Length, 32768);
        if (count > 0 && char.IsHighSurrogate(raw[count - 1])) count--;
        var visible = AgentText.Trim(raw[..count], request.StripAnsi, request.MaxBytes, lineLimit);
        return AgentResponse.Ok(new
        {
            source = "screen", text = visible.Text,
            truncated = count < raw.Length || visible.DroppedCharacters > 0,
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
            var trimmed = AgentText.Trim(page.Text, request.StripAnsi, request.MaxBytes, request.MaxLines);
            return AgentResponse.Ok(new
            {
                text = trimmed.Text,
                // A partially returned page must not advance the client past unseen text.
                cursor = page.Cursor - trimmed.DroppedCharacters,
                oldestCursor = page.OldestCursor,
                latestCursor = page.LatestCursor,
                gap = page.Gap,
                hasMore = page.HasMore || trimmed.DroppedCharacters > 0,
                truncated = trimmed.DroppedCharacters > 0,
                originalCharacters = page.Text.Length,
                returnedCharacters = trimmed.Text.Length,
            });
        }
        catch (ArgumentOutOfRangeException)
        {
            return AgentResponse.Error("invalid_cursor", "The cursor is ahead of this terminal's output.");
        }
    }

    private async Task<AgentResponse> WaitOutputAsync(Grant grant, AgentRequest request, CancellationToken ct, string? expectText = null)
    {
        if (grant.Terminal is null) throw new NotSupportedException();
        if (request.Cursor is < 0) return AgentResponse.Error("invalid_cursor", "The output cursor must be non-negative.");
        var idleMilliseconds = Math.Clamp(request.IdleMilliseconds ?? 500, 50, 5000);
        var timeoutMilliseconds = Math.Clamp(request.TimeoutMilliseconds ?? 30000, 100, 55000);
        var cursor = request.Cursor ?? grant.Terminal.ReadScreen().OutputCursor;
        var text = new StringBuilder();
        long oldestCursor = cursor;
        long latestCursor = cursor;
        var gap = false;
        var hasMore = false;
        var capped = false;
        var total = Stopwatch.StartNew();
        var sinceOutput = Stopwatch.StartNew();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (IsHumanOperating(grant)) return HumanOperatingError();
            TerminalOutputPage page;
            try { page = grant.Terminal.ReadOutput(cursor); }
            catch (ArgumentOutOfRangeException) { return AgentResponse.Error("invalid_cursor", "The cursor is ahead of this terminal's output."); }
            oldestCursor = page.OldestCursor;
            latestCursor = page.LatestCursor;
            gap |= page.Gap;
            if (page.Text.Length > 0)
            {
                text.Append(page.Text);
                cursor = page.Cursor;
                hasMore = page.HasMore;
                sinceOutput.Restart();
                if (expectText is not null && ContainsMarkerValue(text.ToString(), expectText)) break;
                if (text.Length > 512 * 1024) { capped = true; break; }
                if (page.HasMore) continue;
            }
            if (text.Length > 0 && sinceOutput.ElapsedMilliseconds >= idleMilliseconds) break;
            if (total.ElapsedMilliseconds >= timeoutMilliseconds) break;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        var timedOut = total.ElapsedMilliseconds >= timeoutMilliseconds
            && (text.Length == 0 || sinceOutput.ElapsedMilliseconds < idleMilliseconds);
        var rawText = text.ToString();
        // Echo removal needs plain text: PTY mode switches often precede the echo.
        if (request.StripAnsi) rawText = AgentText.StripAnsi(rawText);
        if (grant.LastInputText is { } echoText) rawText = AgentText.StripLeadingEcho(rawText, echoText);
        var trimmed = AgentText.Trim(rawText, request.StripAnsi, request.MaxBytes ?? 65536, request.MaxLines);
        var screen = grant.Terminal.ReadScreen();
        var screenTail = AgentText.Trim(screen.Text, request.StripAnsi, 4096, 4);
        return AgentResponse.Ok(new
        {
            text = trimmed.Text,
            cursor = cursor - trimmed.DroppedCharacters,
            oldestCursor,
            latestCursor,
            gap,
            hasMore = hasMore || trimmed.DroppedCharacters > 0 || capped,
            truncated = trimmed.DroppedCharacters > 0 || capped,
            idle = !timedOut && !capped,
            timedOut,
            elapsedMilliseconds = total.ElapsedMilliseconds,
            commandCompletionKnown = false,
            screen = screen with
            {
                Text = screenTail.Text,
                Truncated = screen.Truncated || screenTail.DroppedCharacters > 0,
            },
        });
    }

    private static bool ContainsMarkerValue(string text, string marker)
    {
        var index = 0;
        while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            var valueStart = index + marker.Length;
            if (valueStart < text.Length
                && (char.IsDigit(text[valueStart])
                    || text[valueStart] == '-' && valueStart + 1 < text.Length && char.IsDigit(text[valueStart + 1])))
                return true;
            index = valueStart;
        }
        return false;
    }

    private async Task<AgentResponse> ExecuteFileAsync(Grant grant, AgentRequest request, CancellationToken ct)
    {
        // Reading access is read-only. Mutating files requires the stronger
        // terminal-control grant, even when the host file policy would allow it.
        if (!grant.Info.TerminalControl && request.Method is ("files.write" or "files.delete" or "files.rename" or "files.mkdir" or "files.upload" or "files.download"))
            return AgentResponse.Error("control_required", "Allow terminal control in the desktop Agent panel before changing remote files.");
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
                        async (stream, token) => await stream.WriteAsync(bytes, token).ConfigureAwait(false), ct).ConfigureAwait(false);
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
                return await UploadFileAsync(files, request, ct).ConfigureAwait(false);
            case "files.download":
                return await DownloadFileAsync(files, request, ct).ConfigureAwait(false);
            default:
                throw new NotSupportedException();
        }
    }

    private static async Task<AgentResponse> UploadFileAsync(IRemoteFileSystem files, AgentRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.LocalPath) || request.LocalPath.Length > 4096 || request.LocalPath.Any(char.IsControl))
            return AgentResponse.Error("invalid_path", "Supply a local upload path without control characters.");
        string localPath;
        try { localPath = Path.GetFullPath(request.LocalPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return AgentResponse.Error("invalid_path", "The local upload path is invalid."); }
        if (!File.Exists(localPath)) return AgentResponse.Error("local_file_not_found", "The local upload file does not exist.");
        if (!request.Path!.StartsWith('/')) return AgentResponse.Error("invalid_path", "A remote upload path must be absolute.");
        var length = new FileInfo(localPath).Length;
        try
        {
            await RemoteFileWrite.WriteAsync(files, request.Path, length, null, request.Overwrite,
                async (stream, token) =>
                {
                    await using var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                    await source.CopyToAsync(stream, 81920, token).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AgentResponse.Error("write_failed", ex.Message);
        }
        return AgentResponse.Ok(new { path = request.Path, localPath, bytesWritten = length, overwrite = request.Overwrite });
    }

    private static async Task<AgentResponse> DownloadFileAsync(IRemoteFileSystem files, AgentRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.LocalPath) || request.LocalPath.Length > 4096 || request.LocalPath.Any(char.IsControl))
            return AgentResponse.Error("invalid_path", "Supply a local download path without control characters.");
        string localPath;
        try { localPath = Path.GetFullPath(request.LocalPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return AgentResponse.Error("invalid_path", "The local download path is invalid."); }
        if (!request.Overwrite && File.Exists(localPath))
            return AgentResponse.Error("local_file_exists", "The local download path already exists. Set overwrite=true to replace it.");
        try
        {
            await using var source = await files.OpenReadAsync(request.Path!, ct).ConfigureAwait(false);
            await using var target = new FileStream(localPath,
                request.Overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await source.CopyToAsync(target, 81920, ct).ConfigureAwait(false);
            var written = target.Length;
            return AgentResponse.Ok(new { path = request.Path, localPath, bytesWritten = written, overwrite = request.Overwrite });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AgentResponse.Error("read_failed", ex.Message);
        }
    }

    private static async Task<AgentResponse> ReadFileAsync(IRemoteFileSystem files, AgentRequest request, CancellationToken ct)
    {
        await using var stream = await files.OpenReadAsync(request.Path!, ct).ConfigureAwait(false);
        if (request.MaxBytes is null && request.Offset is null)
        {
            var legacyText = await BoundedTextFile.ReadAsync(stream, 128 * 1024, ct).ConfigureAwait(false);
            return AgentResponse.Ok(new { path = request.Path, text = legacyText });
        }

        if (request.Offset is < 0) return AgentResponse.Error("invalid_arguments", "The file offset must be non-negative.");
        var offset = request.Offset ?? 0;
        var limit = Math.Clamp(request.MaxBytes ?? 128 * 1024, 1, 512 * 1024);
        if (offset > 0)
        {
            if (stream.CanSeek) stream.Position = offset;
            else
            {
                var skip = offset;
                var buffer = new byte[81920];
                while (skip > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, skip)), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    skip -= read;
                }
            }
        }

        var data = new byte[limit + 1];
        var total = 0;
        while (total < data.Length)
        {
            var read = await stream.ReadAsync(data.AsMemory(total, data.Length - total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        var truncated = total > limit;
        if (truncated) total = limit;
        var text = new UTF8Encoding(false, true).GetString(data, 0, total);
        if (offset == 0 && text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        return AgentResponse.Ok(new { path = request.Path, text, offset, bytesRead = total, truncated, nextOffset = offset + total });
    }

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
        public bool HumanOperating { get; set; }
        public string? LastInputText { get; set; }
        public Dictionary<string, (string Fingerprint, AgentResponse Response)> Inputs { get; } = new(StringComparer.Ordinal);
        public Queue<string> InputOrder { get; } = new();
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
                terminalControl = grant.Info.TerminalControl, isActive = active,
                userOperating = IsHumanOperating(grant),
            }
            : grant.Info with { IsActive = active };
        object screenResult = screen;
        if (compact)
        {
            var tail = AgentText.Trim(screen.Text, stripAnsi: false,
                maxBytes: minimal ? 2048 : 4096, maxLines: minimal ? 3 : 8);
            screenResult = new
            {
                text = tail.Text,
                truncated = screen.Truncated || tail.DroppedCharacters > 0,
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
