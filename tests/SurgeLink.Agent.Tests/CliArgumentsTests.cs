using SurgeLink.Cli;
using Xunit;

namespace SurgeLink.Agent.Tests;

public sealed class CliArgumentsTests
{
    [Fact]
    public void SharedTerminalInputCarriesRevisionAndStableOperationId()
    {
        var input = CliArguments.Parse(["terminal", "send", "--session", "h", "--text", "--literal text", "--submit",
            "--revision", "8", "--operation-id", "write-1", "--check-token", "checked"]);
        Assert.Equal("terminal.text", input.Request.Method);
        Assert.Equal("--literal text", input.Request.Text);
        Assert.Equal(8, input.Request.ExpectedInputRevision);
        Assert.Equal("write-1", input.Request.OperationId);
        Assert.Equal("checked", input.Request.CheckToken);
        Assert.True(input.Request.Submit);
    }

    [Theory]
    [InlineData("terminal send --session h --text command --operation-id x")]
    [InlineData("terminal key --session h --key Enter --revision 0")]
    [InlineData("terminal send --session h --text command --revision 0 --operation-id x")]
    [InlineData("terminal screen --session h --submit")]
    [InlineData("terminal output --session h --cursor -1")]
    [InlineData("sessions info --session h --view files")]
    public void InvalidControlArgumentsAreRejected(string text) =>
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(text.Split(' ')));

    [Theory]
    [InlineData("sessions", "info", "sessions.info")]
    [InlineData("sessions", "check", "sessions.check")]
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
