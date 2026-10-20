namespace SurgeLink.Protocols.Ssh;

/// <summary>Operating system family of the connected guest, as far as monitoring is concerned.</summary>
public enum RemoteOperatingSystem
{
    /// <summary>The remote operating system could not be identified.</summary>
    Unknown = 0,

    /// <summary>A Linux guest; counters come from <c>/proc</c>.</summary>
    Linux = 1,

    /// <summary>A Windows guest; counters come from CIM and the PowerShell net adapter cmdlets.</summary>
    Windows = 2,
}
