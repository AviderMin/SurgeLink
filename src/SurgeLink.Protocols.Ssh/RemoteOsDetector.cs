using System.Text.RegularExpressions;

namespace SurgeLink.Protocols.Ssh;

/// <summary>
/// Classifies the remote guest as Linux or Windows using the output of <c>uname -srm</c>
/// and <c>cmd /c ver</c>. Pure classification logic is separated from probing so it can
/// be unit tested without a server.
/// </summary>
public static partial class RemoteOsDetector
{
    /// <summary>Command used to identify POSIX guests.</summary>
    public const string UnameCommand = "uname -srm";

    /// <summary>Command used to identify Windows guests; it also yields the build number.</summary>
    public const string WindowsVersionCommand = "cmd /c ver";

    /// <summary>Classifies the guest from the two probe outputs.</summary>
    /// <param name="unameOutput">Standard output of <c>uname -srm</c>, when the probe ran.</param>
    /// <param name="windowsVersionOutput">Standard output of <c>cmd /c ver</c>, when the probe ran.</param>
    public static RemoteOperatingSystem Classify(string? unameOutput, string? windowsVersionOutput)
    {
        var windows = windowsVersionOutput?.Trim();
        if (!string.IsNullOrEmpty(windows) && windows.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteOperatingSystem.Windows;
        }

        var uname = unameOutput?.Trim();
        if (string.IsNullOrEmpty(uname))
        {
            return RemoteOperatingSystem.Unknown;
        }

        if (uname.StartsWith("Linux", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteOperatingSystem.Linux;
        }

        // Git for Windows and Cygwin report a Windows kernel through uname.
        if (uname.Contains("MINGW", StringComparison.OrdinalIgnoreCase) ||
            uname.Contains("MSYS", StringComparison.OrdinalIgnoreCase) ||
            uname.Contains("CYGWIN", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteOperatingSystem.Windows;
        }

        return RemoteOperatingSystem.Unknown;
    }

    /// <summary>Extracts the version number from <c>cmd /c ver</c> output, for example <c>10.0.19045.3803</c>.</summary>
    /// <param name="windowsVersionOutput">Standard output of <c>cmd /c ver</c>; localized output is accepted.</param>
    /// <returns>The version text, or <c>null</c> when no numeric version could be found.</returns>
    public static string? ParseWindowsVersion(string? windowsVersionOutput)
    {
        if (string.IsNullOrWhiteSpace(windowsVersionOutput))
        {
            return null;
        }

        var match = WindowsVersionPattern().Match(windowsVersionOutput);
        if (match.Success)
        {
            return match.Groups["version"].Value;
        }

        var fallback = FallbackVersionPattern().Match(windowsVersionOutput);
        return fallback.Success ? fallback.Groups["version"].Value : null;
    }

    /// <summary>
    /// Runs the probes needed to classify the guest. Failures are swallowed so an
    /// unsupported shell degrades to <see cref="RemoteOperatingSystem.Unknown"/>.
    /// </summary>
    /// <param name="executeCommand">Runs a remote command and returns its output.</param>
    /// <param name="cancellationToken">Token used to abandon the probes.</param>
    public static async Task<RemoteOperatingSystem> DetectAsync(
        Func<string, CancellationToken, Task<SshCommandResult>> executeCommand,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executeCommand);
        cancellationToken.ThrowIfCancellationRequested();

        var uname = await TryRunAsync(executeCommand, UnameCommand, cancellationToken).ConfigureAwait(false);
        var windowsVersion = await TryRunAsync(executeCommand, WindowsVersionCommand, cancellationToken).ConfigureAwait(false);
        return Classify(uname, windowsVersion);
    }

    private static async Task<string?> TryRunAsync(
        Func<string, CancellationToken, Task<SshCommandResult>> executeCommand,
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await executeCommand(command, cancellationToken).ConfigureAwait(false);
            var output = result.StandardOutput.Trim();
            return output.Length > 0 ? output : result.CombinedOutput.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\[[^\]\r\n]*?(?<version>\d+(?:\.\d+)+)[^\]\r\n]*\]", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsVersionPattern();

    [GeneratedRegex(@"(?<version>\d+\.\d+(?:\.\d+)+)", RegexOptions.CultureInvariant)]
    private static partial Regex FallbackVersionPattern();
}