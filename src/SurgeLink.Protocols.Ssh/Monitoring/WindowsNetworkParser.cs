using System.Globalization;
using SurgeLink.Core.Monitoring;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>Parses the network sections of a Windows monitor sample.</summary>
public static class WindowsNetworkParser
{
    /// <summary>Parses rows shaped <c>name|receivedBytes|sentBytes|receivedPackets|sentPackets</c>.</summary>
    /// <param name="networkSection">Either the <c>Get-NetAdapterStatistics</c> or the raw CIM fallback section.</param>
    /// <returns>One cumulative <see cref="NetworkSample"/> per adapter.</returns>
    public static IReadOnlyList<NetworkSample> Parse(string? networkSection)
    {
        if (string.IsNullOrWhiteSpace(networkSection))
        {
            return Array.Empty<NetworkSample>();
        }

        var samples = new List<NetworkSample>();
        foreach (var rawLine in networkSection.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split('|');
            if (fields.Length < 3)
            {
                continue;
            }

            var name = fields[0].Trim();
            if (name.Length == 0 ||
                !TryParseCount(fields[1], out var receivedBytes) ||
                !TryParseCount(fields[2], out var sentBytes))
            {
                continue;
            }

            var receivedPackets = fields.Length > 3 && TryParseCount(fields[3], out var packetsReceived) ? packetsReceived : 0;
            var sentPackets = fields.Length > 4 && TryParseCount(fields[4], out var packetsSent) ? packetsSent : 0;

            samples.Add(new NetworkSample
            {
                InterfaceName = name,
                BytesReceived = receivedBytes,
                BytesSent = sentBytes,
                PacketsReceived = receivedPackets,
                PacketsSent = sentPackets,
            });
        }

        return samples;
    }

    private static bool TryParseCount(string text, out long value) =>
        long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;
}
