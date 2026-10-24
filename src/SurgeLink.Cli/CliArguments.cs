using SurgeLink.Agent;

namespace SurgeLink.Cli;

/// <summary>A deliberately small grammar; reject unknown/duplicate options rather than guessing.</summary>
public sealed record CliArguments(AgentRequest Request, int? Instance)
{
    public static CliArguments Parse(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Expected a command and subcommand. Use --help.");
        var method = (args[0], args[1]) switch
        {
            ("sessions", "list") => "sessions.list",
            ("sessions", "info") => "sessions.info",
            ("terminal", "screen") => "terminal.screen",
            ("monitor", "snapshot") => "monitor.snapshot",
            ("files", "list") => "files.list",
            ("files", "read") => "files.read",
            _ => throw new ArgumentException("Unknown command. Use --help."),
        };
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var json = false;
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--json")
            {
                if (json) throw new ArgumentException("Duplicate --json option.");
                json = true;
                continue;
            }
            var key = args[i];
            if (key is not ("--session" or "--path" or "--instance") || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid option: {key}");
            if (!options.TryAdd(key, args[++i])) throw new ArgumentException($"Duplicate option: {key}");
        }
        options.TryGetValue("--session", out var session);
        options.TryGetValue("--path", out var path);
        if (method != "sessions.list" && string.IsNullOrWhiteSpace(session)) throw new ArgumentException("--session is required.");
        if (method == "sessions.list" && session is not null) throw new ArgumentException("sessions list does not accept --session.");
        var fileCommand = method.StartsWith("files.", StringComparison.Ordinal);
        if (fileCommand && string.IsNullOrWhiteSpace(path)) throw new ArgumentException("--path is required.");
        if (!fileCommand && path is not null) throw new ArgumentException("--path is only valid for file commands.");
        int? instance = null;
        if (options.TryGetValue("--instance", out var value))
        {
            if (!int.TryParse(value, out var pid) || pid <= 0) throw new ArgumentException("--instance must be a positive desktop process ID.");
            instance = pid;
        }
        return new(new AgentRequest(method, session, path), instance);
    }
}
