using System.Text;
using Renci.SshNet;

namespace SurgeLink.Protocols.Ssh.Internal;

/// <summary>Runs non interactive commands on an open SSH connection.</summary>
internal sealed class SshCommandRunner
{
    private readonly SshClient _client;
    private readonly Encoding _encoding;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a runner.</summary>
    /// <param name="client">Connected client that owns the session channel.</param>
    /// <param name="encoding">Encoding used to decode command output.</param>
    /// <param name="timeout">Timeout applied to each command.</param>
    public SshCommandRunner(SshClient client, Encoding encoding, TimeSpan timeout)
    {
        _client = client;
        _encoding = encoding;
        _timeout = timeout;
    }

    /// <summary>Executes one command and captures its output.</summary>
    /// <param name="command">Command text passed to the remote login shell.</param>
    /// <param name="cancellationToken">Token used to abandon the command.</param>
    public async Task<SshCommandResult> RunAsync(string command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);

        using var sshCommand = _client.CreateCommand(command, _encoding);
        sshCommand.CommandTimeout = _timeout;
        var execution = sshCommand.ExecuteAsync(cancellationToken);
        // Drain both SDK pipes while the command runs instead of buffering all output
        // inside SSH.NET until exit. Never return a silently truncated listing/probe.
        var output = ReadBoundedOutputAsync(sshCommand.OutputStream, _encoding, 8 * 1024 * 1024, cancellationToken);
        var error = ReadBoundedOutputAsync(sshCommand.ExtendedOutputStream, _encoding, 2 * 1024 * 1024, cancellationToken);
        var pending = new List<Task> { execution, output, error };
        try
        {
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                await finished.ConfigureAwait(false);
                pending.Remove(finished);
            }
            return new SshCommandResult(command, await output.ConfigureAwait(false), await error.ConfigureAwait(false), sshCommand.ExitStatus);
        }
        catch
        {
            // Closing this command unblocks the other pipe reader and settles ExecuteAsync.
            sshCommand.Dispose();
            try { await Task.WhenAll(execution, output, error).ConfigureAwait(false); }
            catch (Exception) { /* Preserve the original limit/transport/cancellation failure. */ }
            throw;
        }
    }

    /// <summary>Streams a file without decoding, capturing the whole payload, or
    /// imposing the short monitoring-command deadline on a large transfer.</summary>
    internal async Task CopyToAsync(string command, Stream destination, CancellationToken ct)
    {
        using var sshCommand = _client.CreateCommand(command, _encoding);
        sshCommand.CommandTimeout = Timeout.InfiniteTimeSpan;
        var execution = sshCommand.ExecuteAsync(ct);
        // A large file may take longer than the command timeout, but a FIFO that
        // replaced the file, a half-open peer or a stalled channel must not hold
        // the transfer lock until the connection is closed. Bound the wait before
        // the first byte and any later read separately.
        var output = CopyWithIdleTimeoutAsync(sshCommand.OutputStream, destination,
            FirstByteTimeout, ReadIdleTimeout, ct);
        var error = ReadBoundedOutputAsync(sshCommand.ExtendedOutputStream, _encoding, 2 * 1024 * 1024, ct);
        try
        {
            // The transfer itself is bounded by first-byte and idle timeouts. Once
            // stdout reaches EOF, bound the command's exit and stderr drain too:
            // a lost exit status or a child that inherited stderr must not hold the
            // transfer lock forever.
            await output.ConfigureAwait(false);
            await WaitForCompletionAsync(execution, error, CommandExitTimeout, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (sshCommand.ExitStatus != 0) throw new IOException("The remote file read command failed.");
        }
        catch
        {
            // We own the command and its output pipes, so disposal unblocks
            // reads even when the peer ignores cancellation or disconnects.
            sshCommand.Dispose();
            try { await Task.WhenAll(execution, output, error).ConfigureAwait(false); }
            catch (Exception) { /* Preserve the original transfer failure. */ }
            throw;
        }
    }

    internal static readonly TimeSpan FirstByteTimeout = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan CommandExitTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Waits for the command exit and the stderr drain after stdout EOF.</summary>
    internal static async Task WaitForCompletionAsync(Task execution, Task error, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var settled = Task.WhenAll(execution, error);
        try
        {
            await settled.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"The remote file read command did not exit within {timeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>Copies bytes and fails when the source stays silent for too long.</summary>
    internal static async Task CopyWithIdleTimeoutAsync(Stream source, Stream destination,
        TimeSpan firstByteTimeout, TimeSpan idleTimeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(firstByteTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);

        var buffer = new byte[32 * 1024];
        var waitingForFirstByte = true;
        while (true)
        {
            var timeout = waitingForFirstByte ? firstByteTimeout : idleTimeout;
            var read = source.ReadAsync(buffer, ct).AsTask();
            int count;
            try
            {
                // Task.WaitAsync owns and cleans up its timeout timer; a completed
                // read does not leave an uncancelled Task.Delay behind per chunk.
                count = await read.WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                ct.ThrowIfCancellationRequested();
                // The abandoned read is completed by disposing the SSH command.
                // Observe a later fault so it does not surface as unhandled.
                _ = read.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw new TimeoutException(
                    $"The remote file read produced no data for {timeout.TotalSeconds:0} seconds.");
            }

            if (count == 0) return;
            await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            waitingForFirstByte = false;
        }
    }

    internal static async Task<string> ReadBoundedOutputAsync(Stream input, Encoding encoding, int maximumBytes, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        using var captured = new MemoryStream();
        var buffer = new byte[Math.Min(81920, maximumBytes)];
        while (true)
        {
            var remaining = maximumBytes - (int)captured.Length + 1;
            var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
            if (count == 0) break;
            if (captured.Length + count > maximumBytes) throw new IOException("SSH command output exceeds the capture limit.");
            captured.Write(buffer, 0, count);
        }
        ct.ThrowIfCancellationRequested();
        var bytes = captured.ToArray();
        var preamble = encoding.GetPreamble();
        // A stream may start with its encoding's signature, but arbitrary bytes or
        // decoded U+FEFF content elsewhere must never be mistaken for a signature.
        var start = preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
        return encoding.GetString(bytes, start, bytes.Length - start);
    }
}
