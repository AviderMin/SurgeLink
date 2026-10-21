using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class WindowsDiskIoParserTests
{
    [Fact]
    public void ParseSumsLogicalDiskRates()
    {
        const string section = """
            C:|1048576|524288
            D:|2097152|0
            malformed
            E:|bad|1
            """;

        var rates = WindowsDiskIoParser.Parse(section);

        Assert.Equal(3145728, rates.ReadBytesPerSecond);
        Assert.Equal(524288, rates.WriteBytesPerSecond);
    }

    [Fact]
    public void ParseReturnsZeroForUnusableInput()
    {
        Assert.Equal(new DiskIoRates(0, 0), WindowsDiskIoParser.Parse(null));
        Assert.Equal(new DiskIoRates(0, 0), WindowsDiskIoParser.Parse(""));
    }
}
