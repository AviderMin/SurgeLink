namespace SurgeLink.Protocols.Ssh;

/// <summary>Result of one non interactive command executed over the SSH connection.</summary>
/// <param name="Command">The command text that was executed.</param>
/// <param name="StandardOutput">Standard output decoded with the negotiated encoding.</param>
/// <param name="StandardError">Standard error decoded with the negotiated encoding.</param>
/// <param name="ExitStatus">Exit status reported by the server, or <c>null</c> when the server reported none.</param>
public sealed record SshCommandResult(string Command, string StandardOutput, string StandardError, int? ExitStatus)
{
    /// <summary>True when the server reported a successful (zero) exit status.</summary>
    public bool Succeeded => ExitStatus == 0;

    /// <summary>Standard output followed by standard error; used for diagnostics and fallback parsing.</summary>
    public string CombinedOutput =>
        string.IsNullOrEmpty(StandardError) ? StandardOutput : StandardOutput + StandardError;
}
