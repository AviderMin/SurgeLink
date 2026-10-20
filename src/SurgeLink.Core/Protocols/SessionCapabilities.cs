namespace SurgeLink.Core.Protocols;

/// <summary>
/// Optional feature sets a protocol session can expose to the shell.
/// The UI only renders the tabs a session actually supports, so adding a
/// protocol never requires touching the shell.
/// </summary>
[Flags]
public enum SessionCapabilities
{
    None = 0,
    Terminal = 1 << 0,
    FileSystem = 1 << 1,
    Monitor = 1 << 2,
}
