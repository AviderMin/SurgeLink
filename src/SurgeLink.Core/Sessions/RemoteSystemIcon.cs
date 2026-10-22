using System.Text.RegularExpressions;

namespace SurgeLink.Core.Sessions;

/// <summary>Maps discovered OS descriptions to bundled icon identifiers, never host names.</summary>
public static partial class RemoteSystemIcon
{
    public static string Resolve(string? operatingSystem)
    {
        if (string.IsNullOrWhiteSpace(operatingSystem)) { return "unknown"; }
        var words = Words().Matches(operatingSystem.ToLowerInvariant()).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
        if (words.Contains("windows")) { return "windows"; }
        if (words.Contains("darwin") || words.Contains("macos") || operatingSystem.Contains("mac os", StringComparison.OrdinalIgnoreCase)) { return "apple"; }
        if (words.Contains("ubuntu")) { return "ubuntu"; }
        if (words.Contains("linuxmint") || words.Contains("mint")) { return "linuxmint"; }
        if (words.Contains("debian")) { return "debian"; }
        if (words.Contains("fedora")) { return "fedora"; }
        if (words.Contains("centos")) { return "centos"; }
        if (words.Contains("rocky")) { return "rockylinux"; }
        if (words.Contains("almalinux") || words.Contains("alma")) { return "almalinux"; }
        if (words.Contains("arch") || words.Contains("archlinux")) { return "archlinux"; }
        if (words.Contains("opensuse")) { return "opensuse"; }
        if (words.Contains("alpine")) { return "alpinelinux"; }
        if (words.Contains("kali")) { return "kalilinux"; }
        if (words.Contains("freebsd")) { return "freebsd"; }
        if (words.Contains("rhel") || operatingSystem.Contains("red hat", StringComparison.OrdinalIgnoreCase)) { return "redhat"; }
        if (words.Contains("linux")) { return "linux"; }
        return "unknown";
    }

    [GeneratedRegex("[a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();
}
