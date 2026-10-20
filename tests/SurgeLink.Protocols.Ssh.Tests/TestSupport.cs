using System.Text;
using SurgeLink.Protocols.Ssh;
using SurgeLink.Protocols.Ssh.Monitoring;

namespace SurgeLink.Protocols.Ssh.Tests;

/// <summary>Command executor double used by the Windows monitor tests.</summary>
internal sealed class TestCommandExecutor
{
    private readonly Func<string, CancellationToken, Task<SshCommandResult>> _handler;

    public TestCommandExecutor(Func<string, CancellationToken, Task<SshCommandResult>> handler) =>
        _handler = handler;

    public TestCommandExecutor(Func<string, Task<SshCommandResult>> handler)
        : this((command, _) => handler(command))
    {
    }

    public List<string> Commands { get; } = new();

    public int Count => Commands.Count;

    public Task<SshCommandResult> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        Commands.Add(command);
        cancellationToken.ThrowIfCancellationRequested();
        return _handler(command, cancellationToken);
    }

    public static TestCommandExecutor Returning(Func<string, string> outputFactory) =>
        new((command, _) => Task.FromResult(new SshCommandResult(command, outputFactory(command), string.Empty, 0)));
}

/// <summary>Builds delimited monitor output for probe tests.</summary>
internal static class TestMonitorOutput
{
    public static string Build(params (string Name, string Content)[] sections)
    {
        var builder = new StringBuilder();
        foreach (var (name, content) in sections)
        {
            builder.Append(MonitorOutput.MarkerPrefix).Append(name).Append('\n');
            if (content.Length > 0)
            {
                builder.Append(content);
                if (!content.EndsWith('\n'))
                {
                    builder.Append('\n');
                }
            }
        }

        return builder.ToString();
    }
}