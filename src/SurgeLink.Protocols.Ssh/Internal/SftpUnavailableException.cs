namespace SurgeLink.Protocols.Ssh.Internal;

/// <summary>Only initial SFTP subsystem negotiation can authorize the SCP fallback.</summary>
internal sealed class SftpUnavailableException(Exception inner) : Exception("The SFTP subsystem is unavailable.", inner);
