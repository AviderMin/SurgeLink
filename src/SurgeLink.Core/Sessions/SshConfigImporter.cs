using SurgeLink.Core.Protocols;

namespace SurgeLink.Core.Sessions;

/// <summary>Result of importing profiles from an OpenSSH config file.</summary>
public sealed record SshConfigImportResult(
    IReadOnlyList<SessionProfile> Imported,
    int SkippedDuplicates,
    string SourcePath);

/// <summary>
/// Parses standard OpenSSH <c>~/.ssh/config</c> files into <see cref="SessionProfile"/> entries.
/// Wildcard-only <c>Host</c> blocks supply fallback defaults; concrete aliases become profiles.
/// </summary>
public static class SshConfigImporter
{
    public const string DefaultGroup = "SSH Config";

    /// <summary>Returns the default user OpenSSH config path (<c>~/.ssh/config</c>).</summary>
    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ssh",
        "config");

    /// <summary>Reads <paramref name="path"/> and returns only profiles not already present in <paramref name="existing"/>.</summary>
    public static SshConfigImportResult ImportFromFile(
        string path,
        IReadOnlyList<SessionProfile> existing,
        string? userProfileDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(existing);
        if (!File.Exists(path))
        {
            return new SshConfigImportResult(Array.Empty<SessionProfile>(), 0, path);
        }

        var content = File.ReadAllText(path);
        var parsed = Parse(content, userProfileDirectory);
        var imported = new List<SessionProfile>();
        var skipped = 0;

        foreach (var candidate in parsed)
        {
            var isDuplicate = existing.Any(e => IsSameProfile(e, candidate))
                || imported.Any(e => IsSameProfile(e, candidate));
            if (isDuplicate)
            {
                skipped++;
                continue;
            }

            imported.Add(candidate);
        }

        return new SshConfigImportResult(imported, skipped, path);
    }

    /// <summary>Parses OpenSSH config text into SSH <see cref="SessionProfile"/> objects.</summary>
    public static IReadOnlyList<SessionProfile> Parse(string content, string? userProfileDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<SessionProfile>();
        }

        var home = string.IsNullOrWhiteSpace(userProfileDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfileDirectory;

        var defaults = new HostBlock();
        var blocks = new List<(List<string> Aliases, HostBlock Values)>();
        List<HostBlock> active = [];

        foreach (var rawLine in content.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;

            if (!TrySplitDirective(line, out var key, out var value)) continue;

            if (string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                var tokens = SplitTokens(value);
                var concrete = new List<string>();
                var hasGlobalWildcard = false;
                foreach (var token in tokens)
                {
                    if (token == "*") { hasGlobalWildcard = true; continue; }
                    if (token.StartsWith('!') || token.Contains('*') || token.Contains('?')) continue;
                    concrete.Add(token);
                }

                active = [];
                if (concrete.Count > 0)
                {
                    var block = new HostBlock();
                    blocks.Add((concrete, block));
                    active.Add(block);
                }
                if (hasGlobalWildcard && concrete.Count == 0)
                {
                    active.Add(defaults);
                }
                continue;
            }

            if (string.Equals(key, "Match", StringComparison.OrdinalIgnoreCase))
            {
                // Match blocks are conditional and cannot be mapped statically.
                active = [];
                continue;
            }

            foreach (var target in active)
            {
                ApplyDirective(target, key, value, home);
            }
        }

        var results = new List<SessionProfile>();
        var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (aliases, block) in blocks)
        {
            foreach (var alias in aliases)
            {
                if (!seenAliases.Add(alias)) continue;

                var host = FirstNonEmpty(block.HostName, defaults.HostName, alias)!;
                var user = FirstNonEmpty(block.User, defaults.User, Environment.UserName) ?? "root";
                var port = block.Port ?? defaults.Port ?? 22;
                var identityFile = FirstNonEmpty(block.IdentityFile, defaults.IdentityFile);
                var proxyJump = FirstNonEmpty(block.ProxyJump, defaults.ProxyJump);

                var forwards = new List<string>();
                forwards.AddRange(block.Forwards);
                foreach (var f in defaults.Forwards)
                {
                    if (!forwards.Contains(f, StringComparer.Ordinal)) forwards.Add(f);
                }

                var options = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(proxyJump) && !string.Equals(proxyJump, "none", StringComparison.OrdinalIgnoreCase))
                {
                    options["proxyJump"] = proxyJump;
                }
                if (forwards.Count > 0)
                {
                    options["portForwards"] = string.Join(';', forwards);
                }

                results.Add(new SessionProfile
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Name = alias,
                    ProtocolId = "ssh",
                    Host = host,
                    Port = port,
                    Username = user,
                    Authentication = string.IsNullOrWhiteSpace(identityFile)
                        ? AuthenticationMethod.Password
                        : AuthenticationMethod.PublicKey,
                    PrivateKeyPath = identityFile,
                    Group = DefaultGroup,
                    Options = options,
                });
            }
        }

        return results;
    }

    private static void ApplyDirective(HostBlock target, string key, string value, string home)
    {
        switch (key.ToLowerInvariant())
        {
            case "hostname":
                target.HostName ??= Unquote(value);
                break;
            case "user":
                target.User ??= Unquote(value);
                break;
            case "port":
                if (target.Port is null && int.TryParse(Unquote(value), out var port) && port is > 0 and <= 65535)
                {
                    target.Port = port;
                }
                break;
            case "identityfile":
                target.IdentityFile ??= ExpandHome(Unquote(value), home);
                break;
            case "proxyjump":
                target.ProxyJump ??= Unquote(value);
                break;
            case "localforward":
                if (TryFormatTwoArgForward("L", value, out var localSpec)) target.Forwards.Add(localSpec);
                break;
            case "remoteforward":
                if (TryFormatTwoArgForward("R", value, out var remoteSpec)) target.Forwards.Add(remoteSpec);
                break;
            case "dynamicforward":
                if (TryFormatDynamicForward(value, out var dynSpec)) target.Forwards.Add(dynSpec);
                break;
        }
    }

    private static bool TryFormatTwoArgForward(string kind, string value, out string spec)
    {
        spec = string.Empty;
        var tokens = SplitTokens(value);
        if (tokens.Count != 2) return false;
        var bind = tokens[0];
        var dest = tokens[1];
        var bindPort = bind.Contains(':') ? bind[(bind.LastIndexOf(':') + 1)..] : bind;
        var destColon = dest.LastIndexOf(':');
        if (destColon <= 0 || destColon >= dest.Length - 1) return false;
        if (!int.TryParse(bindPort, out var lp) || lp is <= 0 or > 65535) return false;
        if (!int.TryParse(dest[(destColon + 1)..], out var rp) || rp is <= 0 or > 65535) return false;
        var host = dest[..destColon].Trim('[', ']');
        spec = $"{kind}:{lp}:{host}:{rp}";
        return true;
    }

    private static bool TryFormatDynamicForward(string value, out string spec)
    {
        spec = string.Empty;
        var token = SplitTokens(value).FirstOrDefault();
        if (string.IsNullOrEmpty(token)) return false;
        var portPart = token.Contains(':') ? token[(token.LastIndexOf(':') + 1)..] : token;
        if (!int.TryParse(portPart, out var port) || port is <= 0 or > 65535) return false;
        spec = $"D:{port}";
        return true;
    }

    private static string ExpandHome(string path, string home)
    {
        if (string.IsNullOrEmpty(home)) return path;
        if (path == "~") return home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(home, path[2..]);
        }
        return path;
    }

    private static bool IsSameProfile(SessionProfile left, SessionProfile right) =>
        string.Equals(left.ProtocolId, right.ProtocolId, StringComparison.OrdinalIgnoreCase)
        && (string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
                && left.ResolvePort(22) == right.ResolvePort(22)
                && string.Equals(left.Username, right.Username, StringComparison.OrdinalIgnoreCase)));

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

    private static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (c == '#' && !inQuotes) return line[..i];
        }
        return line;
    }

    private static bool TrySplitDirective(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        var index = 0;
        while (index < line.Length && !char.IsWhiteSpace(line[index]) && line[index] != '=') index++;
        if (index == 0 || index >= line.Length) return false;
        key = line[..index];
        while (index < line.Length && (char.IsWhiteSpace(line[index]) || line[index] == '=')) index++;
        if (index >= line.Length) return false;
        value = line[index..].Trim();
        return value.Length > 0;
    }

    private static List<string> SplitTokens(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in text)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    private sealed class HostBlock
    {
        public string? HostName { get; set; }
        public string? User { get; set; }
        public int? Port { get; set; }
        public string? IdentityFile { get; set; }
        public string? ProxyJump { get; set; }
        public List<string> Forwards { get; } = [];
    }
}
