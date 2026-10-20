namespace SurgeLink.Core.Sessions;

/// <summary>
/// Facts discovered about the remote host after the transport is up.
/// Every field is optional: protocols fill in what they can cheaply discover.
/// </summary>
public sealed record RemoteHostInfo
{
    /// <summary>An empty descriptor used before the host is probed.</summary>
    public static RemoteHostInfo Empty { get; } = new();

    public string? HostName { get; init; }

    public string? OperatingSystem { get; init; }

    public string? KernelVersion { get; init; }

    public string? Architecture { get; init; }

    public string? HomeDirectory { get; init; }

    public string? Shell { get; init; }

    public string? ServerVersion { get; init; }

    /// <summary>Short single line summary for the status bar.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>(4);
            if (!string.IsNullOrWhiteSpace(HostName)) parts.Add(HostName!);
            if (!string.IsNullOrWhiteSpace(OperatingSystem)) parts.Add(OperatingSystem!);
            if (!string.IsNullOrWhiteSpace(Architecture)) parts.Add(Architecture!);
            return parts.Count == 0 ? "Unknown host" : string.Join(" · ", parts);
        }
    }
}
