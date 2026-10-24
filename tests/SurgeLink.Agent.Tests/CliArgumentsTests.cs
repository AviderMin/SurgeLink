using SurgeLink.Cli;
using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class CliArgumentsTests
{
    [Theory]
    [InlineData("sessions", "info", "sessions.info")]
    [InlineData("terminal", "screen", "terminal.screen")]
    [InlineData("monitor", "snapshot", "monitor.snapshot")]
    public void SessionCommandsHaveExplicitHandleAndInstance(string command, string subcommand, string method)
    {
        var parsed = CliArguments.Parse([command, subcommand, "--session", "handle", "--instance", "123", "--json"]);
        Assert.Equal(method, parsed.Request.Method);
        Assert.Equal("handle", parsed.Request.Session);
        Assert.Equal(123, parsed.Instance);
    }

    [Fact]
    public void FilePathsRemainOneLiteralArgument()
    {
        var parsed = CliArguments.Parse(["files", "read", "--session", "handle", "--path", "/tmp/a b;$(touch pwned)"]);
        Assert.Equal("/tmp/a b;$(touch pwned)", parsed.Request.Path);
    }

    [Theory]
    [InlineData("terminal screen")]
    [InlineData("sessions list --instance 0")]
    [InlineData("sessions list --instance 1 --instance 2")]
    [InlineData("sessions list --session secret")]
    [InlineData("sessions list --path /tmp")]
    [InlineData("sessions list --json --json")]
    [InlineData("sessions list --unknown")]
    [InlineData("files read --session handle")]
    [InlineData("files delete --session handle --path /tmp")]
    public void InvalidArgumentsAreRejected(string arguments) =>
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(arguments.Split(' ')));
}
