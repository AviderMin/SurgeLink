using System.Text;
using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class WindowsMonitorScriptTests
{
    private const string CommandPrefix = "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ";

    [Fact]
    public void SampleCommandUsesEncodedCommand()
    {
        Assert.StartsWith(CommandPrefix, WindowsMonitorScript.SampleCommand);
    }

    [Fact]
    public void SampleCommandDecodesBackToTheScript()
    {
        var payload = WindowsMonitorScript.SampleCommand[CommandPrefix.Length..];
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(payload));

        Assert.Equal(WindowsMonitorScript.Script, decoded);
    }

    [Fact]
    public void ScriptCollectsEveryRequiredCounter()
    {
        var script = WindowsMonitorScript.Script;

        Assert.Contains("Win32_PerfFormattedData_PerfOS_Processor", script, StringComparison.Ordinal);
        Assert.Contains("Win32_Processor", script, StringComparison.Ordinal);
        Assert.Contains("Win32_OperatingSystem", script, StringComparison.Ordinal);
        Assert.Contains("Win32_PerfFormattedData_PerfOS_System", script, StringComparison.Ordinal);
        Assert.Contains("Win32_LogicalDisk", script, StringComparison.Ordinal);
        Assert.Contains("Win32_PerfFormattedData_PerfDisk_LogicalDisk", script, StringComparison.Ordinal);
        Assert.Contains("Get-NetAdapterStatistics", script, StringComparison.Ordinal);
        Assert.Contains("Win32_PerfRawData_Tcpip_NetworkInterface", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptEmitsEverySectionMarker()
    {
        foreach (var section in new[]
                 {
                     WindowsMonitorScript.CpuSection,
                     WindowsMonitorScript.ProcessorSection,
                     WindowsMonitorScript.MemorySection,
                     WindowsMonitorScript.SystemSection,
                     WindowsMonitorScript.DiskSection,
                     WindowsMonitorScript.DiskIoSection,
                     WindowsMonitorScript.NetworkSection,
                     WindowsMonitorScript.NetworkFallbackSection,
                     WindowsMonitorScript.EndSection,
                 })
        {
            Assert.Contains("Emit '" + section + "'", WindowsMonitorScript.Script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryScriptForcesUtf8ConsoleOutput()
    {
        foreach (var script in new[]
                 {
                     WindowsMonitorScript.Script,
                     WindowsMonitorScript.BuildPerfProcessScript(10),
                     WindowsMonitorScript.BuildGetProcessScript(10),
                 })
        {
            Assert.Contains("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8", script, StringComparison.Ordinal);
            Assert.Contains("$ProgressPreference = 'SilentlyContinue'", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ScriptContainsNoDoubleQuotedExpansions()
    {
        Assert.False(WindowsMonitorScript.Script.Contains('"', StringComparison.Ordinal));
        Assert.False(WindowsMonitorScript.BuildPerfProcessScript(10).Contains('"', StringComparison.Ordinal));
        Assert.False(WindowsMonitorScript.BuildGetProcessScript(10).Contains('"', StringComparison.Ordinal));
    }

    [Fact]
    public void EncodeUsesUtf16LittleEndianBase64()
    {
        const string script = "Get-Date";

        Assert.Equal(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), WindowsMonitorScript.Encode(script));
    }

    [Fact]
    public void BuildCommandRejectsEmptyScripts()
    {
        Assert.ThrowsAny<ArgumentException>(() => WindowsMonitorScript.BuildCommand(string.Empty));
        Assert.Throws<ArgumentNullException>(() => WindowsMonitorScript.BuildCommand(null!));
        Assert.Throws<ArgumentNullException>(() => WindowsMonitorScript.Encode(null!));
    }

    [Fact]
    public void BuildTopProcessCommandsReturnsThePerfCommandThenTheFallback()
    {
        var commands = WindowsMonitorScript.BuildTopProcessCommands(25);

        Assert.Equal(2, commands.Count);
        Assert.StartsWith(CommandPrefix, commands[0]);
        Assert.StartsWith(CommandPrefix, commands[1]);

        var first = Decode(commands[0]);
        var second = Decode(commands[1]);
        Assert.Contains("Win32_PerfFormattedData_PerfProc_Process", first, StringComparison.Ordinal);
        Assert.Contains("-First 25", first, StringComparison.Ordinal);
        Assert.Contains("Get-Process", second, StringComparison.Ordinal);
        Assert.Contains("-First 25", second, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "-First 1")]
    [InlineData(-5, "-First 1")]
    [InlineData(9999, "-First 500")]
    public void BuildTopProcessCommandsClampsTheLimit(int requested, string expected)
    {
        var commands = WindowsMonitorScript.BuildTopProcessCommands(requested);

        Assert.Contains(expected, Decode(commands[0]), StringComparison.Ordinal);
        Assert.Contains(expected, Decode(commands[1]), StringComparison.Ordinal);
    }

    private static string Decode(string command) =>
        Encoding.Unicode.GetString(Convert.FromBase64String(command[CommandPrefix.Length..]));
}