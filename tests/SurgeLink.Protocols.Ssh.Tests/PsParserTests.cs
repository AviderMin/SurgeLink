using SurgeLink.Core.Monitoring;
using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class PsParserTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParseBoundsRowsAndFieldsBeforeRetainingThem(bool header)
    {
        var text = (header ? "PID USER %CPU RSS COMMAND\n" : "")
            + string.Concat(Enumerable.Range(1, 2000).Select(i => $"{i} user 1 9223372036854775807 {new string('x', 5000)}\n"));
        var rows = PsParser.Parse(text);
        Assert.Equal(PsParser.MaximumProcessCount, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.True(row.CommandLine?.Length <= 4096);
            Assert.True(row.Name.Length <= 4096);
            Assert.True(row.ResidentBytes >= 0);
        });
    }

    [Fact]
    public void BoundedParsingStillFindsHighCpuProcessesLateInUnsortedOutput()
    {
        var text = "PID USER %CPU COMMAND\n" + string.Concat(Enumerable.Range(1, 1000).Select(i => $"{i} root {i} worker\n"));
        var top = PsParser.TopByCpu(PsParser.Parse(text), 1);
        Assert.Equal(1000, Assert.Single(top).ProcessId);
    }
    private const string PsEoOutput = """
            PID USER     %CPU %MEM   RSS COMMAND
              1 root      0.0  0.1  1672 systemd
            412 root      0.5  0.3 123456 python3
            900 alice    12.5  2.5 456789 node
            901 alice     3.0  0.2  90000 /usr/sbin/nginx -g daemon off;
        """;

    private const string PsAuxOutput = """
        USER       PID %CPU %MEM    VSZ   RSS TTY      STAT START   TIME COMMAND
        root         1  0.0  0.1 168236  8964 ?        Ss   Oct19   0:02 /sbin/init splash
        www-data  1234  2.3  1.2 1234567 98765 ?      S    10:01   1:23 nginx: worker process
        """;

    private const string BusyBoxOutput = """
        PID   USER     TIME  COMMAND
            1 root      0:00 /sbin/init
          123 root      0:01 /usr/sbin/sshd -D
        """;

    [Fact]
    public void ParseReadsPsEoColumns()
    {
        var processes = PsParser.Parse(PsEoOutput);

        Assert.Equal(4, processes.Count);
        var root = processes[0];
        Assert.Equal(1, root.ProcessId);
        Assert.Equal("root", root.User);
        Assert.Equal("systemd", root.Name);
        Assert.Equal(1672L * 1024, root.ResidentBytes);

        var node = processes[2];
        Assert.Equal(12.5, node.CpuPercent, 6);
        Assert.Equal(2.5, node.MemoryPercent, 6);
        Assert.Equal(456789L * 1024, node.ResidentBytes);
        Assert.Equal("node", node.CommandLine);
        Assert.Equal("alice", node.User);

        var nginx = processes[3];
        Assert.Equal("nginx", nginx.Name);
        Assert.Equal("/usr/sbin/nginx -g daemon off;", nginx.CommandLine);
    }

    [Fact]
    public void ParseReadsPsAuxColumnsIncludingCommandWithSpaces()
    {
        var processes = PsParser.Parse(PsAuxOutput);

        Assert.Equal(2, processes.Count);
        Assert.Equal("/sbin/init splash", processes[0].CommandLine);
        Assert.Equal(8964L * 1024, processes[0].ResidentBytes);
        Assert.Equal("init", processes[0].Name);

        Assert.Equal(1234, processes[1].ProcessId);
        Assert.Equal(2.3, processes[1].CpuPercent, 6);
        Assert.Equal("nginx: worker process", processes[1].CommandLine);
    }

    [Fact]
    public void ParseReadsBusyBoxColumnsWithoutCpuPercent()
    {
        var processes = PsParser.Parse(BusyBoxOutput);

        Assert.Equal(2, processes.Count);
        Assert.Equal(1, processes[0].ProcessId);
        Assert.Equal("root", processes[0].User);
        Assert.Equal("/sbin/init", processes[0].CommandLine);
        Assert.Equal(0d, processes[0].CpuPercent);
    }

    [Fact]
    public void ParseSkipsUnparsableRows()
    {
        const string text = """
            PID USER %CPU %MEM RSS COMMAND
            not a process row
            9999
               1 root 1.0 0.1 100 ok
            """;

        var processes = PsParser.Parse(text);

        Assert.Single(processes);
        Assert.Equal(1, processes[0].ProcessId);
    }

    [Fact]
    public void ParseUnderstandsPsEfHeader()
    {
        const string text = """
            UID        PID  PPID  C STIME TTY          TIME CMD
            root         1     0  0 Oct19 ?        00:00:02 /sbin/init
            root       123     1  0 Oct19 ?        00:00:00 /usr/sbin/sshd -D
            """;

        var processes = PsParser.Parse(text);

        Assert.Equal(2, processes.Count);
        Assert.Equal(1, processes[0].ProcessId);
        Assert.Equal("/usr/sbin/sshd -D", processes[1].CommandLine);
    }

    [Fact]
    public void ParseReturnsEmptyForHeaderOnlyOrUnusableInput()
    {
        Assert.Empty(PsParser.Parse("PID USER %CPU %MEM RSS COMMAND"));
        Assert.Empty(PsParser.Parse(null));
        Assert.Empty(PsParser.Parse(string.Empty));
        Assert.Empty(PsParser.Parse("garbage"));
    }

    [Fact]
    public void TopByCpuOrdersDescendingAndCaps()
    {
        var processes = new[]
        {
            CreateProcess(1, 5d),
            CreateProcess(2, 50d),
            CreateProcess(3, 50d),
            CreateProcess(4, 0d),
        };

        var top = PsParser.TopByCpu(processes, 2);

        Assert.Equal(2, top.Count);
        Assert.Equal(2, top[0].ProcessId);
        Assert.Equal(3, top[1].ProcessId);
    }

    [Fact]
    public void TopByCpuReturnsEmptyForNonPositiveCount()
    {
        var processes = new[] { CreateProcess(1, 5d) };

        Assert.Empty(PsParser.TopByCpu(processes, 0));
        Assert.Empty(PsParser.TopByCpu(processes, -3));
    }

    [Fact]
    public void TopByCpuCapsAtTheConfiguredMaximum()
    {
        var processes = Enumerable.Range(1, 600).Select(index => CreateProcess(index, index)).ToArray();

        var top = PsParser.TopByCpu(processes, 1000);

        Assert.Equal(PsParser.MaximumProcessCount, top.Count);
        Assert.Equal(600, top[0].ProcessId);
    }

    [Fact]
    public void TopByCpuRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => PsParser.TopByCpu(null!, 5));
    }

    private static RemoteProcessInfo CreateProcess(int processId, double cpuPercent) => new()
    {
        ProcessId = processId,
        Name = "process" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        CpuPercent = cpuPercent,
    };
}
