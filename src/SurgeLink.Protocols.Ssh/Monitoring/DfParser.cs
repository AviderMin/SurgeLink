using System.Globalization;
using SurgeLink.Core.Monitoring;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>Parses <c>df -kP</c> output into mount point usage.</summary>
public static class DfParser
{
    private const long BytesPerKilobyte = 1024;

    private static readonly string[] PseudoFileSystemPrefixes =
    {
        "tmpfs", "devtmpfs", "udev", "shm", "none", "proc", "sysfs", "devpts", "cgroup", "mqueue",
        "hugetlbfs", "ramfs", "securityfs", "debugfs", "tracefs", "configfs", "pstore", "bpf",
        "autofs", "rpc_pipefs", "nfsd", "fusectl", "selinuxfs", "efivarfs", "binfmt_misc",
    };

    /// <summary>Parses the rows of a <c>df -kP</c> snapshot, including rows where df wrapped a long device name.</summary>
    /// <param name="text">Raw command output; header and error lines are ignored.</param>
    /// <param name="includePseudoFileSystems">True to keep tmpfs and similar in-memory file systems.</param>
    /// <returns>One disk sample per real mount point, in command order.</returns>
    public static IReadOnlyList<DiskSample> Parse(string? text, bool includePseudoFileSystems = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<DiskSample>();
        }

        var samples = new List<DiskSample>();
        string? wrappedDevice = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("Filesystem", StringComparison.OrdinalIgnoreCase))
            {
                wrappedDevice = null;
                continue;
            }

            if (line.StartsWith("df:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 1)
            {
                wrappedDevice = tokens[0];
                continue;
            }

            if (tokens.Length < 5)
            {
                continue;
            }

            string device;
            var offset = 1;
            if (wrappedDevice is not null)
            {
                device = wrappedDevice;
                offset = 0;
                wrappedDevice = null;
            }
            else
            {
                device = tokens[0];
            }

            if (tokens.Length - offset < 5)
            {
                continue;
            }

            if (!TryParseLong(tokens[offset], out var blocks) ||
                !TryParseLong(tokens[offset + 1], out var used) ||
                !TryParseLong(tokens[offset + 2], out var available))
            {
                continue;
            }

            if (!includePseudoFileSystems && IsPseudoFileSystem(device))
            {
                continue;
            }

            samples.Add(new DiskSample
            {
                MountPoint = tokens[offset + 4],
                FileSystem = device,
                TotalBytes = blocks * BytesPerKilobyte,
                UsedBytes = used * BytesPerKilobyte,
                AvailableBytes = available * BytesPerKilobyte,
            });
        }

        return samples;
    }

    /// <summary>Returns true when <paramref name="device"/> is a virtual file system that has no real disk usage.</summary>
    /// <param name="device">Device column of a <c>df</c> row.</param>
    public static bool IsPseudoFileSystem(string? device)
    {
        if (string.IsNullOrWhiteSpace(device))
        {
            return false;
        }

        var name = device!.Trim();
        foreach (var prefix in PseudoFileSystemPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseLong(string token, out long value) =>
        long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
