using System.Globalization;
using System.Text;
using SurgeLink.Core.Files;
using SurgeLink.Core.Protocols;

namespace SurgeLink.Protocols.Ssh.Internal;

/// <summary>Framed metadata whose file identities never depend on display-oriented ls output.</summary>
internal static class ScpMetadata
{
    private const string Header = "SURGELINK-FS-1";
    private static readonly Encoding PathEncoding = new UTF8Encoding(false, true);

    // Paths and link targets are hex-encoded before entering the ASCII wire
    // format. NUL fields and an explicit terminator detect partial responses.
    // GNU/BusyBox stat are supported; missing tools fail closed, never to ls.
    private const string Functions = """
            export LC_ALL=C
            hex() {
                encoded=$(printf '%s' "$1" | od -An -v -tx1) || return 1
                printf '%s' "$encoded" | tr -d ' \n'
            }
            emit() {
                metadata=$(stat -c '%f %s %Y %u %g' -- "$1") || return 1
                identity=$(hex "$1") || return 1
                target=''
                if [ -L "$1" ]; then
                    target=$(readlink -n -- "$1" && printf '.') || return 1
                    target=${target%.}
                    target=$(hex "$target") || return 1
                fi
                printf '%s\000%s\000%s\000' "$metadata" "$identity" "$target"
            }
            """;

    internal static string BuildCommand(string path, bool directory)
    {
        var script = Functions + "\nprintf 'SURGELINK-FS-1\\000'\np=" + Quote(path) + "\n";
        if (directory)
        {
            script += """
                [ -d "$p" ] && [ -r "$p" ] && [ -x "$p" ] || exit 1
                root=${p%/}
                for entry in "$root"/* "$root"/.[!.]* "$root"/..?*; do
                    if [ -e "$entry" ] || [ -L "$entry" ]; then
                        emit "$entry" || exit 1
                    fi
                done
                """;
        }
        else { script += "emit \"$p\" || exit 1\n"; }
        script += "\nprintf 'END\\000'\n";
        return "sh -c " + Quote(script);
    }

    internal static string BuildHomeCommand() => "sh -c " + Quote(Functions + "\n" + """
        home=$(pwd -P && printf '.') || exit 1
        home=${home%.}
        home=${home%?}
        identity=$(hex "$home") || exit 1
        printf 'SURGELINK-PWD-1\000%s\000END\000' "$identity"
        """);

    internal static string ParseHome(string output)
    {
        var fields = output.Split('\0');
        if (fields.Length != 4 || fields[0] != "SURGELINK-PWD-1" || fields[2] != "END" || fields[3].Length != 0)
            throw InvalidMetadata();
        var path = Decode(fields[1]);
        if (!path.StartsWith('/')) { throw InvalidMetadata(); }
        return path;
    }

    internal static IReadOnlyList<RemoteFileEntry> Parse(string path, string output, bool directory)
    {
        var fields = output.Split('\0');
        if (fields.Length < 3 || fields[0] != Header || fields[^2] != "END" || fields[^1].Length != 0
            || (fields.Length - 3) % 3 != 0) { throw InvalidMetadata(); }
        var entries = new List<RemoteFileEntry>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var prefix = path.TrimEnd('/') + "/";
        for (var index = 1; index < fields.Length - 2; index += 3)
        {
            var metadata = fields[index].Split(' ');
            if (metadata.Length != 5
                || !int.TryParse(metadata[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var mode)
                || mode < 0 || mode > 0xFFFF
                || !long.TryParse(metadata[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || !long.TryParse(metadata[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var modified)
                || !uint.TryParse(metadata[3], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                || !uint.TryParse(metadata[4], NumberStyles.None, CultureInfo.InvariantCulture, out _)) { throw InvalidMetadata(); }
            var fullPath = Decode(fields[index + 1]);
            string name;
            if (directory)
            {
                if (!fullPath.StartsWith(prefix, StringComparison.Ordinal)) { throw InvalidMetadata(); }
                name = fullPath[prefix.Length..];
                if (name.Length == 0 || name is "." or ".." || name.Contains('/')) { throw InvalidMetadata(); }
            }
            else
            {
                if (!string.Equals(fullPath, path, StringComparison.Ordinal)) { throw InvalidMetadata(); }
                name = path == "/" ? "/" : path[(path.LastIndexOf('/') + 1)..];
            }
            if (!identities.Add(fullPath)) { throw InvalidMetadata(); }
            var kind = (mode & 0xF000) switch
            {
                0x8000 => RemoteEntryKind.File,
                0x4000 => RemoteEntryKind.Directory,
                0xA000 => RemoteEntryKind.SymbolicLink,
                0x1000 or 0x2000 or 0x6000 or 0xC000 => RemoteEntryKind.Other,
                _ => throw InvalidMetadata(),
            };
            var target = Decode(fields[index + 2]);
            if ((kind == RemoteEntryKind.SymbolicLink) != (target.Length != 0)) { throw InvalidMetadata(); }
            DateTimeOffset timestamp;
            try { timestamp = DateTimeOffset.FromUnixTimeSeconds(modified); }
            catch (ArgumentOutOfRangeException) { throw InvalidMetadata(); }
            entries.Add(new RemoteFileEntry
            {
                Name = name, FullPath = fullPath, Kind = kind, Size = kind == RemoteEntryKind.Directory ? 0 : size,
                UnixMode = mode & 0xFFF, ModifiedUtc = timestamp, Owner = metadata[3], Group = metadata[4],
                LinkTarget = kind == RemoteEntryKind.SymbolicLink ? target : null,
            });
        }
        if (!directory && entries.Count != 1) { throw InvalidMetadata(); }
        return entries;
    }

    private static string Decode(string hex)
    {
        try
        {
            var text = PathEncoding.GetString(Convert.FromHexString(hex));
            if (text.Contains('\0')) { throw InvalidMetadata(); }
            return text;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException) { throw InvalidMetadata(); }
    }

    private static ProtocolException InvalidMetadata() => new(
        "SCP metadata is incomplete or cannot safely identify UTF-8 file paths. No entries were returned.",
        ProtocolFailureKind.ServerRejected);

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
