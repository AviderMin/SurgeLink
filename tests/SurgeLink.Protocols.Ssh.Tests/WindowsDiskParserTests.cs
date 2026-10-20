using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class WindowsDiskParserTests
{
    [Fact]
    public void ParseReadsLogicalDisks()
    {
        const string section = """
            C:|536870912000|268435456000|NTFS
            D:|107374182400|107374182400|
            """;

        var disks = WindowsDiskParser.Parse(section);

        Assert.Equal(2, disks.Count);
        Assert.Equal("C:", disks[0].MountPoint);
        Assert.Equal(536870912000, disks[0].TotalBytes);
        Assert.Equal(268435456000, disks[0].UsedBytes);
        Assert.Equal(268435456000, disks[0].AvailableBytes);
        Assert.Equal("NTFS", disks[0].FileSystem);

        Assert.Equal("D:", disks[1].MountPoint);
        Assert.Equal(0, disks[1].UsedBytes);
        Assert.Null(disks[1].FileSystem);
    }

    [Fact]
    public void ParseClampsFreeSpaceToTheDiskSize()
    {
        const string section = "C:|1000|5000|NTFS\n";

        var disks = WindowsDiskParser.Parse(section);

        Assert.Single(disks);
        Assert.Equal(0, disks[0].UsedBytes);
        Assert.Equal(1000, disks[0].AvailableBytes);
    }

    [Theory]
    [InlineData("C:|0|0|NTFS", 0)]
    [InlineData("C:|abc|100|NTFS", 0)]
    [InlineData("C:|100|abc|NTFS", 0)]
    [InlineData("C:|100", 0)]
    [InlineData("|100|50|NTFS", 0)]
    [InlineData("E:|100|50|NTFS", 1)]
    public void ParseSkipsUnusableRows(string section, int expectedCount)
    {
        Assert.Equal(expectedCount, WindowsDiskParser.Parse(section).Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void ParseReturnsEmptyForBlankSections(string? section)
    {
        Assert.Empty(WindowsDiskParser.Parse(section));
    }
}
