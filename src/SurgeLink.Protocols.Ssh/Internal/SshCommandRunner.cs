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
        await sshCommand.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        return new SshCommandResult(
            command,
            sshCommand.Result ?? string.Empty,
            sshCommand.Error ?? string.Empty,
            sshCommand.ExitStatus);
    }
}
