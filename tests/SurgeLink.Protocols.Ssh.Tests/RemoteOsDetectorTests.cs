using SurgeLink.Protocols.Ssh;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class RemoteOsDetectorTests
{
    [Theory]
    [InlineData("Linux 5.15.0-91-generic x86_64", null, RemoteOperatingSystem.Linux)]
    [InlineData("Linux 6.1.0 arm64", "sh: 1: cmd: not found", RemoteOperatingSystem.Linux)]
    [InlineData(null, "Microsoft Windows [Version 10.0.19045.3803]", RemoteOperatingSystem.Windows)]
    [InlineData(null, "  Microsoft Windows [Version 10.0.22631.4317]\r\n", RemoteOperatingSystem.Windows)]
    [InlineData(null, "Microsoft Windows Server 2025 [Version 10.0.26100.2033]", RemoteOperatingSystem.Windows)]
    [InlineData("MINGW64_NT-10.0-19045 host 3.4.10 x86_64", null, RemoteOperatingSystem.Windows)]
    [InlineData("MSYS_NT-10.0 host 3.4.10 x86_64", null, RemoteOperatingSystem.Windows)]
    [InlineData("CYGWIN_NT-10.0 host 3.4.10 x86_64", null, RemoteOperatingSystem.Windows)]
    [InlineData("Darwin 23.0.0 arm64", null, RemoteOperatingSystem.Unknown)]
    [InlineData(null, null, RemoteOperatingSystem.Unknown)]
    [InlineData("", "shell syntax error", RemoteOperatingSystem.Unknown)]
    [InlineData("FreeBSD 14.0-RELEASE amd64", null, RemoteOperatingSystem.Unknown)]
    public void ClassifyIdentifiesTheGuestFamily(string? uname, string? windowsVersion, RemoteOperatingSystem expected)
    {
        Assert.Equal(expected, RemoteOsDetector.Classify(uname, windowsVersion));
    }

    [Theory]
    [InlineData("Microsoft Windows [Version 10.0.19045.3803]", "10.0.19045.3803")]
    [InlineData("Microsoft Windows [版本 10.0.22631.4317]", "10.0.22631.4317")]
    [InlineData("Microsoft Windows [Version 6.3.9600]", "6.3.9600")]
    [InlineData("Version 10.0.19045.3803", "10.0.19045.3803")]
    [InlineData("no version here", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseWindowsVersionExtractsTheBuildNumber(string? output, string? expected)
    {
        Assert.Equal(expected, RemoteOsDetector.ParseWindowsVersion(output));
    }

    [Fact]
    public async Task DetectAsyncClassifiesALinuxGuest()
    {
        var executor = new TestCommandExecutor(command => Task.FromResult(command == RemoteOsDetector.UnameCommand
            ? new SshCommandResult(command, "Linux 5.15.0-91-generic x86_64", string.Empty, 0)
            : new SshCommandResult(command, string.Empty, "sh: 1: cmd: not found", 127)));

        var operatingSystem = await RemoteOsDetector.DetectAsync(executor.ExecuteAsync, CancellationToken.None);

        Assert.Equal(RemoteOperatingSystem.Linux, operatingSystem);
        Assert.Equal(2, executor.Count);
    }

    [Fact]
    public async Task DetectAsyncClassifiesAWindowsGuest()
    {
        var executor = new TestCommandExecutor(command => Task.FromResult(command == RemoteOsDetector.WindowsVersionCommand
            ? new SshCommandResult(command, "Microsoft Windows [Version 10.0.19045.3803]", string.Empty, 0)
            : new SshCommandResult(command, string.Empty, "'uname' is not recognized", 1)));

        var operatingSystem = await RemoteOsDetector.DetectAsync(executor.ExecuteAsync, CancellationToken.None);

        Assert.Equal(RemoteOperatingSystem.Windows, operatingSystem);
    }

    [Fact]
    public async Task DetectAsyncUsesStandardErrorWhenStandardOutputIsEmpty()
    {
        var executor = new TestCommandExecutor(command => Task.FromResult(command == RemoteOsDetector.WindowsVersionCommand
            ? new SshCommandResult(command, string.Empty, "Microsoft Windows [Version 10.0.22631.4317]", 0)
            : new SshCommandResult(command, string.Empty, string.Empty, 1)));

        var operatingSystem = await RemoteOsDetector.DetectAsync(executor.ExecuteAsync, CancellationToken.None);

        Assert.Equal(RemoteOperatingSystem.Windows, operatingSystem);
    }

    [Fact]
    public async Task DetectAsyncReturnsUnknownWhenEveryProbeFails()
    {
        var executor = new TestCommandExecutor((_, _) => Task.FromException<SshCommandResult>(new InvalidOperationException("channel closed")));

        var operatingSystem = await RemoteOsDetector.DetectAsync(executor.ExecuteAsync, CancellationToken.None);

        Assert.Equal(RemoteOperatingSystem.Unknown, operatingSystem);
    }

    [Fact]
    public async Task DetectAsyncHonoursCancellation()
    {
        var executor = TestCommandExecutor.Returning(_ => string.Empty);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RemoteOsDetector.DetectAsync(executor.ExecuteAsync, cancellation.Token));
    }

    [Fact]
    public async Task DetectAsyncRejectsNullExecutor()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => RemoteOsDetector.DetectAsync(null!, CancellationToken.None));
    }
}