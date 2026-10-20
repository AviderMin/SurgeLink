using System.Globalization;
using SurgeLink.Core.Monitoring;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>Parses <c>/proc/net/dev</c> into cumulative interface counters.</summary>
public static class NetDevParser
{
    private const int MinimumColumnCount = 10;

    /// <summary>Parses the interface lines of a <c>/proc/net/dev</c> snapshot.</summary>
    /// <param name="text">Raw <c>/proc/net/dev</c> contents; header lines are ignored.</param>
    /// <returns>One network sample per interface, in file order.</returns>
    public static IReadOnlyList<NetworkSample> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<NetworkSample>();
        }

        var samples = new List<NetworkSample>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var interfaceName = line[..colon].Trim();
            if (interfaceName.Length == 0)
            {
                continue;
            }

            var tokens = line[(colon + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < MinimumColumnCount)
            {
                continue;
            }

            if (!TryParseLong(tokens[0], out var receivedBytes) ||
                !TryParseLong(tokens[1], out var receivedPackets) ||
                !TryParseLong(tokens[8], out var sentBytes) ||
                !TryParseLong(tokens[9], out var sentPackets))
            {
                continue;
            }

            samples.Add(new NetworkSample
            {
                InterfaceName = interfaceName,
                BytesReceived = receivedBytes,
                PacketsReceived = receivedPackets,
                BytesSent = sentBytes,
                PacketsSent = sentPackets,
            });
        }

        return samples;
    }

    private static bool TryParseLong(string token, out long value) =>
        long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;
}
