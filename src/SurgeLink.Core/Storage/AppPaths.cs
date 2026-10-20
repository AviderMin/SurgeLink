namespace SurgeLink.Core.Storage;

/// <summary>
/// Canonical on-disk locations. Everything the app persists lives under a
/// single folder so uninstalling is a directory delete.
/// </summary>
public static class AppPaths
{
    /// <summary>Creates (once) and returns <c>%LOCALAPPDATA%\SurgeLink</c>.</summary>
    public static string Root { get; } = EnsureDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurgeLink"));

    /// <summary>Saved session profiles.</summary>
    public static string SessionsFile => Path.Combine(Root, "sessions.json");

    /// <summary>DPAPI protected credential blobs.</summary>
    public static string CredentialsFile => Path.Combine(Root, "credentials.json");

    /// <summary>Trusted host key fingerprints.</summary>
    public static string HostKeysFile => Path.Combine(Root, "known-hosts.json");

    /// <summary>Per-run diagnostic logs.</summary>
    public static string LogsDirectory => EnsureDirectory(Path.Combine(Root, "logs"));

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
