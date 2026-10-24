using SurgeLink.Agent;

namespace SurgeLink.Cli;

/// <summary>Explicit command-scoped options; never execute a local shell.</summary>
public sealed record CliArguments(AgentRequest Request, int? Instance)
{
    public static CliArguments Parse(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Expected a command and subcommand. Use --help.");
        var method = (args[0], args[1]) switch
        {
            ("sessions", "list") => "sessions.list",
            ("sessions", "info") => "sessions.info",
            ("sessions", "activate") => "sessions.activate",
            ("terminal", "screen") => "terminal.screen",
            ("terminal", "output") => "terminal.output",
            ("terminal", "send") => "terminal.text",
            ("terminal", "key") => "terminal.key",
            ("monitor", "snapshot") => "monitor.snapshot",
            ("files", "list") => "files.list",
            ("files", "read") => "files.read",
            _ => throw new ArgumentException("Unknown command. Use --help."),
        };
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "--instance" };
        if (method != "sessions.list") allowed.Add("--session");
        if (method.StartsWith("files.", StringComparison.Ordinal)) allowed.Add("--path");
        if (method == "sessions.activate") allowed.Add("--view");
        if (method == "terminal.output") allowed.Add("--cursor");
        if (method is "terminal.text" or "terminal.key")
        {
            allowed.UnionWith(["--revision", "--operation-id"]);
            allowed.Add(method == "terminal.text" ? "--text" : "--key");
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i++)
        {
            var key = args[i];
            if (key == "--json" || key == "--submit" && method == "terminal.text")
            {
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
        if (method is "terminal.text" or "terminal.key")
        {
            if (revision is null) throw new ArgumentException("--revision from terminal screen is required.");
            operationId = Require("--operation-id");
            if (method == "terminal.text" && !options.ContainsKey("--text")) throw new ArgumentException("--text is required.");
            if (method == "terminal.key") Require("--key");
        }
        return new(new AgentRequest(method, session, path, Text: Get("--text"), Submit: flags.Contains("--submit"),
            Key: Get("--key"), Cursor: Number("--cursor"), ExpectedInputRevision: revision, OperationId: operationId, View: Get("--view")), instance);
    }
}
