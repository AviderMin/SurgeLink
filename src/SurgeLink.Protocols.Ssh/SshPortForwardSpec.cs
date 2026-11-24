using SurgeLink.Core.Protocols;

namespace SurgeLink.Protocols.Ssh;

/// <summary>SSH port forwarding direction.</summary>
public enum SshPortForwardKind
{
    Local,
    Remote,
    Dynamic,
}

/// <summary>Parsed SSH port forwarding specification (<c>L</c>, <c>R</c>, or <c>D</c>).</summary>
public sealed record SshPortForwardSpec(
    SshPortForwardKind Kind,
    string BindAddress,
    uint BindPort,
    string? TargetHost,
    uint TargetPort)
{
    /// <summary>
    /// Parses a semicolon- or comma-delimited list of port forwarding specs:
    /// <c>L:[bindHost:]localPort:remoteHost:remotePort</c>,
    /// <c>R:[bindHost:]remotePort:localHost:localPort</c>, or
    /// <c>D:[bindHost:]localPort</c>.
    /// </summary>
    public static IReadOnlyList<SshPortForwardSpec> ParseList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<SshPortForwardSpec>();
        }

        var result = new List<SshPortForwardSpec>();
        foreach (var entry in raw.Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Length == 0) continue;
            result.Add(ParseSingle(entry));
        }

        return result;
    }

    /// <summary>Parses one port forwarding specification.</summary>
    public static SshPortForwardSpec ParseSingle(string entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);
        var parts = entry.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            throw new ProtocolException($"Invalid port forwarding specification '{entry}'.", ProtocolFailureKind.Configuration);
        }

        var prefix = parts[0].ToUpperInvariant();
        switch (prefix)
        {
            case "L" or "R":
                var kind = prefix == "L" ? SshPortForwardKind.Local : SshPortForwardKind.Remote;
                if (parts.Length == 4)
                {
                    var bindPort = ParsePort(parts[1], entry);
                    var targetHost = RequireHost(parts[2], entry);
                    var targetPort = ParsePort(parts[3], entry);
                    return new SshPortForwardSpec(kind, "127.0.0.1", bindPort, targetHost, targetPort);
                }
                if (parts.Length == 5)
                {
                    var bindHost = string.IsNullOrEmpty(parts[1]) ? "127.0.0.1" : parts[1];
                    var bindPort = ParsePort(parts[2], entry);
                    var targetHost = RequireHost(parts[3], entry);
                    var targetPort = ParsePort(parts[4], entry);
                    return new SshPortForwardSpec(kind, bindHost, bindPort, targetHost, targetPort);
                }
                throw new ProtocolException($"Invalid {prefix} port forwarding specification '{entry}'.", ProtocolFailureKind.Configuration);

            case "D":
                if (parts.Length == 2)
                {
                    return new SshPortForwardSpec(SshPortForwardKind.Dynamic, "127.0.0.1", ParsePort(parts[1], entry), null, 0);
                }
                if (parts.Length == 3)
                {
                    var bindHost = string.IsNullOrEmpty(parts[1]) ? "127.0.0.1" : parts[1];
                    return new SshPortForwardSpec(SshPortForwardKind.Dynamic, bindHost, ParsePort(parts[2], entry), null, 0);
                }
                throw new ProtocolException($"Invalid D port forwarding specification '{entry}'.", ProtocolFailureKind.Configuration);

            default:
                throw new ProtocolException($"Unsupported port forwarding type '{parts[0]}' in '{entry}'. Use L, R, or D.", ProtocolFailureKind.Configuration);
        }
    }

    private static uint ParsePort(string text, string entry)
    {
        if (!uint.TryParse(text, out var port) || port is 0 or > 65535)
        {
            throw new ProtocolException($"Invalid port '{text}' in '{entry}'.", ProtocolFailureKind.Configuration);
        }
        return port;
    }

    private static string RequireHost(string host, string entry)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ProtocolException($"Missing target host in '{entry}'.", ProtocolFailureKind.Configuration);
        }
        return host;
    }
}

/// <summary>Parsed SSH ProxyJump bastion endpoint (<c>[user@]host[:port]</c>).</summary>
public sealed record SshJumpTarget(string Host, int Port, string Username)
{
    public static SshJumpTarget Parse(string raw, string defaultUsername)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        var text = raw.Trim();
        var user = defaultUsername;
        var at = text.LastIndexOf('@');
        if (at >= 0)
        {
            user = text[..at].Trim();
            text = text[(at + 1)..].Trim();
            if (string.IsNullOrEmpty(user))
            {
                throw new ProtocolException($"Invalid ProxyJump user in '{raw}'.", ProtocolFailureKind.Configuration);
            }
        }

        string host;
        int port = SshProtocolPlugin.DefaultPort;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close <= 1)
            {
                throw new ProtocolException($"Invalid IPv6 ProxyJump host in '{raw}'.", ProtocolFailureKind.Configuration);
            }
            host = text[1..close];
            if (close + 1 < text.Length)
            {
                if (text[close + 1] != ':' || !int.TryParse(text[(close + 2)..], out port) || port is <= 0 or > 65535)
                {
                    throw new ProtocolException($"Invalid ProxyJump port in '{raw}'.", ProtocolFailureKind.Configuration);
                }
            }
        }
        else
        {
            var colon = text.LastIndexOf(':');
            if (colon > 0 && text.IndexOf(':') == colon)
            {
                host = text[..colon].Trim();
                if (!int.TryParse(text[(colon + 1)..], out port) || port is <= 0 or > 65535)
                {
                    throw new ProtocolException($"Invalid ProxyJump port in '{raw}'.", ProtocolFailureKind.Configuration);
                }
            }
            else
            {
                host = text;
            }
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ProtocolException($"Invalid ProxyJump host in '{raw}'.", ProtocolFailureKind.Configuration);
        }

        return new SshJumpTarget(host, port, user);
    }
}
