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

    [Fact]
    public void WaitAndFileOptionsParseWithoutChangingExistingCommands()
    {
        var wait = CliArguments.Parse(["terminal", "wait", "--session", "h", "--cursor", "12", "--idle-ms", "250",
            "--timeout-ms", "5000", "--strip-ansi", "--max-lines", "30"]);
        Assert.Equal("terminal.wait", wait.Request.Method);
        Assert.Equal(12L, wait.Request.Cursor);
        Assert.Equal(250, wait.Request.IdleMilliseconds);
        Assert.Equal(5000, wait.Request.TimeoutMilliseconds);
        Assert.True(wait.Request.StripAnsi);
        Assert.Equal(30, wait.Request.MaxLines);

        var write = CliArguments.Parse(["files", "write", "--session", "h", "--path", "/tmp/a", "--text-file", "C:\\tmp\\a.txt", "--overwrite"]);
        Assert.Equal("files.write", write.Request.Method);
        Assert.Equal("C:\\tmp\\a.txt", write.TextFile);
        Assert.True(write.Request.Overwrite);

        var read = CliArguments.Parse(["files", "read", "--session", "h", "--path", "/tmp/a", "--offset", "10", "--max-bytes", "2048"]);
        Assert.Equal(10L, read.Request.Offset);
        Assert.Equal(2048, read.Request.MaxBytes);

        var check = CliArguments.Parse(["sessions", "check", "--session", "h", "--brief"]);
        Assert.True(check.Request.Brief);

        var upload = CliArguments.Parse(["files", "upload", "--session", "h", "--path", "/tmp/a", "--local-path", "C:\\tmp\\a.txt"]);
        Assert.Equal("files.upload", upload.Request.Method);
        Assert.Equal("C:\\tmp\\a.txt", upload.Request.LocalPath);

        var leadingDashPath = CliArguments.Parse(["files", "read", "--session", "h", "--path", "--leading.log", "--tail-bytes", "4096"]);
        Assert.Equal("--leading.log", leadingDashPath.Request.Path);
        Assert.Equal(4096, leadingDashPath.Request.TailBytes);

        var leadingDashDest = CliArguments.Parse(["files", "rename", "--session", "h", "--path", "--src.log", "--destination", "--dest.log"]);
        Assert.Equal("--src.log", leadingDashDest.Request.Path);
        Assert.Equal("--dest.log", leadingDashDest.Request.Destination);

        var exec = CliArguments.Parse(["terminal", "exec", "--session", "h", "--text", "ls", "--revision", "0", "--operation-id", "op1", "--check-token", "tok", "--max-bytes", "4096", "--max-lines", "50"]);
        Assert.Equal("terminal.exec", exec.Request.Method);
        Assert.Equal(4096, exec.Request.MaxBytes);
        Assert.Equal(50, exec.Request.MaxLines);
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
    [InlineData("files read --session handle --path --max-bytes 100")]
    [InlineData("files read --session handle --path /tmp/a --offset 10 --tail-bytes 10")]
    [InlineData("files read --session handle --path /tmp/a --tail-bytes 0")]
    [InlineData("files read --session handle --path /tmp/a --tail-bytes -1")]
    [InlineData("files chmod --session handle --path /tmp")]
    [InlineData("terminal exec --session h --text cmd --revision 0 --operation-id x --check-token t --idle-ms 500")]
    [InlineData("terminal send --session h --text cmd --text-stdin --revision 0 --operation-id x --check-token t")]
    [InlineData("terminal send --session h --text-file a.txt --text-stdin --revision 0 --operation-id x --check-token t")]
    [InlineData("files write --session h --path /tmp/a --text hi --text-stdin")]
    [InlineData("terminal wait --session h --idle-ms 0")]
    [InlineData("terminal wait --session h --timeout-ms 0")]
    [InlineData("terminal output --session h --max-bytes 0")]
    [InlineData("terminal output --session h --max-lines 0")]
    [InlineData("terminal screen --session h --tail-rows 0")]
    [InlineData("files list --session h --path / --limit 0")]
    [InlineData("files read --session h --path /tmp/a --max-bytes 10 --tail-bytes 10")]
    public void InvalidArgumentsAreRejected(string arguments) =>
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(arguments.Split(' ')));
}
