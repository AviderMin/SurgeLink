using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class WindowsProcessParserTests
{
    [Fact]
    public void ParseReadsPerfProcessRows()
    {
        const string output = """
            100|chrome#1|12.5|1048576|40
            200|notepad|0.0|2048|4
            """;

        var processes = WindowsProcessParser.Parse(output);

        Assert.Equal(2, processes.Count);
        Assert.Equal(100, processes[0].ProcessId);
        Assert.Equal("chrome", processes[0].Name);
        Assert.Equal(12.5, processes[0].CpuPercent, 6);
        Assert.Equal(1048576, processes[0].ResidentBytes);

        Assert.Equal("notepad", processes[1].Name);
    }

    [Fact]
    public void ParseFiltersIdleAndTotalRows()
    {
        const string output = """
            0|Idle|90.0|0|8
            4|_Total|50.0|0|100
            5|realprocess|1.0|10|2
            """;

        var processes = WindowsProcessParser.Parse(output);

        Assert.Single(processes);
        Assert.Equal("realprocess", processes[0].Name);
    }

    [Fact]
    public void ParseSkipsRowsWithAnInvalidProcessId()
    {
        const string output = """
            abc|broken|1.0|10|2
            0|zero|1.0|10|2
            -3|negative|1.0|10|2
            5|ok|1.0|10|2
            """;

        var processes = WindowsProcessParser.Parse(output);

        Assert.Single(processes);
        Assert.Equal(5, processes[0].ProcessId);
    }

    [Fact]
    public void ParseAcceptsRowsWithoutThreadColumn()
    {
        const string output = "77|GetProcessRow|3.5|4096\n";

        var processes = WindowsProcessParser.Parse(output);

        Assert.Single(processes);
        Assert.Equal(77, processes[0].ProcessId);
        Assert.Equal(3.5, processes[0].CpuPercent, 6);
        Assert.Equal(4096, processes[0].ResidentBytes);
    }

    [Fact]
    public void ParseClampsPercentagesAndSkipsMalformedNumbers()
    {
        const string output = """
            9|hot|999999|1|1
            10|cold|-50|1|1
            11|odd|not-a-number|abc|1
            """;

        var processes = WindowsProcessParser.Parse(output);

        Assert.Equal(3, processes.Count);
        Assert.Equal(10000d, processes[0].CpuPercent, 6);
        Assert.Equal(0d, processes[1].CpuPercent, 6);
        Assert.Equal(0d, processes[2].CpuPercent, 6);
        Assert.Equal(0, processes[2].ResidentBytes);
    }

    [Fact]
    public void ParseKeepsProcessNamesWithDotsAndSpaces()
    {
        const string output = """
            21|my service.exe|1.0|1|1
            22|name#with#hash|1.0|1|1
            """;

        var processes = WindowsProcessParser.Parse(output);

        Assert.Equal(2, processes.Count);
        Assert.Equal("my service.exe", processes[0].Name);
        Assert.Equal("name#with#hash", processes[1].Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("no|pipes|here")]
    public void ParseReturnsEmptyForNoUsableRows(string? output)
    {
        Assert.Empty(WindowsProcessParser.Parse(output));
    }

    [Fact]
    public void ParseKeepsASingleValidRow()
    {
        var processes = WindowsProcessParser.Parse("1|name|1.0|2|3");

        Assert.Single(processes);
        Assert.Equal("name", processes[0].Name);
    }
}