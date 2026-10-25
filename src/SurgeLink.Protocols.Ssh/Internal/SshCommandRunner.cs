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
        var output = sshCommand.OutputStream.CopyToAsync(destination, 32 * 1024, ct);
        var error = ReadBoundedOutputAsync(sshCommand.ExtendedOutputStream, _encoding, 2 * 1024 * 1024, ct);
        var pending = new List<Task> { execution, output, error };
        try
        {
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                await finished.ConfigureAwait(false);
                pending.Remove(finished);
            }
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
        using var reader = new StreamReader(new MemoryStream(captured.ToArray(), writable: false), encoding, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}
