using SurgeLink.Agent;

namespace SurgeLink.Cli;

/// <summary>Explicit command-scoped options; never execute a local shell.</summary>
public sealed record CliArguments(AgentRequest Request, int? Instance, string? TextFile = null, bool TextFromStdin = false)
{
    public static CliArguments Parse(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Expected a command and subcommand. Use --help.");
        var method = (args[0], args[1]) switch
        {
            ("sessions", "list") => "sessions.list",
            ("sessions", "info") => "sessions.info",
            ("sessions", "check") => "sessions.check",
            ("sessions", "activate") => "sessions.activate",
            ("terminal", "screen") => "terminal.screen",
            ("terminal", "output") => "terminal.output",
            ("terminal", "wait") => "terminal.wait",
            ("terminal", "send") => "terminal.text",
            ("terminal", "key") => "terminal.key",
            ("monitor", "snapshot") => "monitor.snapshot",
            ("files", "list") => "files.list",
            ("files", "read") => "files.read",
            ("files", "stat") => "files.stat",
            ("files", "write") => "files.write",
            ("files", "delete") => "files.delete",
            ("files", "rename") => "files.rename",
            ("files", "mkdir") => "files.mkdir",
            ("files", "upload") => "files.upload",
            ("files", "download") => "files.download",
            _ => throw new ArgumentException("Unknown command. Use --help."),
        };
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "--instance" };
        if (method != "sessions.list") allowed.Add("--session");
        if (method.StartsWith("files.", StringComparison.Ordinal)) allowed.Add("--path");
        if (method == "sessions.check") allowed.Add("--brief");
        if (method == "sessions.activate") allowed.Add("--view");
        if (method == "terminal.screen") allowed.UnionWith(["--strip-ansi", "--tail-rows"]);
        if (method == "terminal.output") allowed.UnionWith(["--cursor", "--strip-ansi", "--max-bytes", "--max-lines"]);
        if (method == "terminal.wait") allowed.UnionWith(["--cursor", "--idle-ms", "--timeout-ms", "--strip-ansi", "--max-bytes", "--max-lines"]);
        if (method == "terminal.text") allowed.UnionWith(["--text-file", "--text-stdin"]);
        if (method is "files.list") allowed.UnionWith(["--offset", "--limit"]);
        if (method is "files.read") allowed.UnionWith(["--offset", "--max-bytes"]);
        if (method is "files.write") allowed.UnionWith(["--text-file", "--text-stdin", "--overwrite"]);
        if (method is "files.delete") allowed.Add("--recursive");
        if (method is "files.rename") allowed.Add("--destination");
        if (method is "files.upload" or "files.download") allowed.Add("--local-path");
        if (method is "terminal.text" or "terminal.key")
        {
            allowed.UnionWith(["--revision", "--operation-id", "--check-token"]);
            allowed.Add(method == "terminal.text" ? "--text" : "--key");
        }
        if (method is "files.write") allowed.Add("--text");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i++)
        {
            var key = args[i];
            if (key is "--json" or "--submit" or "--brief" or "--strip-ansi" or "--overwrite" or "--recursive" or "--text-stdin")
            {
                if (key == "--submit" && method != "terminal.text") throw new ArgumentException("Invalid option: --submit");
                if (key == "--brief" && method != "sessions.check") throw new ArgumentException("Invalid option: --brief");
                if (key == "--strip-ansi" && method is not ("terminal.screen" or "terminal.output" or "terminal.wait")) throw new ArgumentException("Invalid option: --strip-ansi");
                if (key == "--overwrite" && method is not ("files.write" or "files.upload" or "files.download")) throw new ArgumentException("Invalid option: --overwrite");
                if (key == "--recursive" && method != "files.delete") throw new ArgumentException("Invalid option: --recursive");
                if (key == "--text-stdin" && method is not ("terminal.text" or "files.write")) throw new ArgumentException("Invalid option: --text-stdin");
                if (!flags.Add(key)) throw new ArgumentException($"Duplicate option: {key}");
                continue;
            }
            if (!allowed.Contains(key) || i + 1 >= args.Length
                || key is not ("--text" or "--key") && args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid option: {key}");
            if (!options.TryAdd(key, args[++i])) throw new ArgumentException($"Duplicate option: {key}");
        }
        string? Get(string key) => options.GetValueOrDefault(key);
        string Require(string key) => !string.IsNullOrWhiteSpace(Get(key)) ? Get(key)! : throw new ArgumentException($"{key} is required.");
        long? Number(string key)
        {
            if (Get(key) is not { } text) return null;
            if (!long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value))
                throw new ArgumentException($"{key} must be a non-negative integer.");
            return value;
        }
        int? Int(string key)
        {
            if (Number(key) is not long value) return null;
            if (value > int.MaxValue) throw new ArgumentException($"{key} is too large.");
            return (int)value;
        }

        var session = method == "sessions.list" ? null : Require("--session");
        var path = method.StartsWith("files.", StringComparison.Ordinal) ? Require("--path") : null;
        int? instance = null;
        if (Number("--instance") is long pid)
        {
            if (pid <= 0 || pid > int.MaxValue) throw new ArgumentException("--instance must be a positive desktop process ID.");
            instance = (int)pid;
        }
        var revision = Number("--revision");
        var operationId = Get("--operation-id");
        var textFile = Get("--text-file");
        var textFromStdin = flags.Contains("--text-stdin");
        if (textFile is not null && options.ContainsKey("--text")) throw new ArgumentException("--text and --text-file cannot be used together.");
        if (method is "terminal.text" or "terminal.key")
        {
            if (revision is null) throw new ArgumentException("--revision from terminal screen is required.");
            operationId = Require("--operation-id");
            Require("--check-token");
            if (method == "terminal.text" && !options.ContainsKey("--text") && textFile is null && !textFromStdin)
                throw new ArgumentException("--text, --text-file or --text-stdin is required.");
            if (method == "terminal.key") Require("--key");
        }
        if (method == "files.write" && !options.ContainsKey("--text") && textFile is null && !textFromStdin)
            throw new ArgumentException("--text, --text-file or --text-stdin is required.");
        if (method == "files.rename") Require("--destination");
        if (method is "files.upload" or "files.download") Require("--local-path");
        return new(new AgentRequest(method, session, path, Text: Get("--text"), Submit: flags.Contains("--submit"),
            Key: Get("--key"), Cursor: Number("--cursor"), ExpectedInputRevision: revision, OperationId: operationId,
            View: Get("--view"), CheckToken: Get("--check-token"), Brief: flags.Contains("--brief"),
            StripAnsi: flags.Contains("--strip-ansi"), MaxBytes: Int("--max-bytes"), MaxLines: Int("--max-lines"),
            TailRows: Int("--tail-rows"), Offset: Number("--offset"), Limit: Int("--limit"),
            Destination: Get("--destination"), Recursive: flags.Contains("--recursive"),
            Overwrite: flags.Contains("--overwrite"), IdleMilliseconds: Int("--idle-ms"),
            TimeoutMilliseconds: Int("--timeout-ms"), LocalPath: Get("--local-path")), instance, textFile, textFromStdin);
    }
}
