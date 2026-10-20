using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class DfParserTests
{
    private const string Sample = """
        Filesystem     1024-blocks      Used Available Capacity Mounted on
        /dev/sda1         102400000  45000000  52400000      47% /
        tmpfs               8192000         0   8192000       0% /dev/shm
        /dev/mapper/vg-data
                          204800000 100000000 100000000      50% /data
        """;

    [Fact]
    public void ParseReadsRowsAndWrappedDeviceNames()
    {
        var disks = DfParser.Parse(Sample);

        Assert.Equal(2, disks.Count);
        var root = disks[0];
        Assert.Equal("/", root.MountPoint);
        Assert.Equal("/dev/sda1", root.FileSystem);
        Assert.Equal(102400000L * 1024, root.TotalBytes);
        Assert.Equal(45000000L * 1024, root.UsedBytes);
        Assert.Equal(52400000L * 1024, root.AvailableBytes);

        var data = disks[1];
        Assert.Equal("/data", data.MountPoint);
        Assert.Equal("/dev/mapper/vg-data", data.FileSystem);
        Assert.Equal(204800000L * 1024, data.TotalBytes);
        Assert.Equal(100000000d / 204800000d, data.UsedFraction, 12);
    }

    [Fact]
    public void ParseCanIncludePseudoFileSystems()
    {
        var disks = DfParser.Parse(Sample, includePseudoFileSystems: true);

        Assert.Equal(3, disks.Count);
        Assert.Contains(disks, disk => disk.FileSystem == "tmpfs");
    }

    [Fact]
    public void ParseSkipsErrorAndHeaderLines()
    {
        const string text = """
            Filesystem     1024-blocks      Used Available Capacity Mounted on
            df: /run/user/1000/doc: Permission denied
            /dev/sdb1          1000           10        990       1% /mnt/x
            """;

        var disks = DfParser.Parse(text);

        Assert.Single(disks);
        Assert.Equal("/mnt/x", disks[0].MountPoint);
    }

    [Fact]
    public void ParseSkipsRowsWithUnparsableSizes()
    {
        const string text = """
            Filesystem 1024-blocks Used Available Capacity Mounted on
            /dev/sdc1 - - - - /broken
            /dev/sdd1 100 20 80 20% /ok
            """;

        var disks = DfParser.Parse(text);

        Assert.Single(disks);
        Assert.Equal("/ok", disks[0].MountPoint);
    }

    [Theory]
    [InlineData("tmpfs")]
    [InlineData("devtmpfs")]
    [InlineData("none")]
    [InlineData("proc")]
    [InlineData("cgroup2")]
    [InlineData("udev")]
    public void IsPseudoFileSystemDetectsVirtualFileSystems(string device)
    {
        Assert.True(DfParser.IsPseudoFileSystem(device));
    }

    [Theory]
    [InlineData("/dev/sda1")]
    [InlineData("overlay")]
    [InlineData("10.0.0.5:/export")]
    [InlineData(null)]
    public void IsPseudoFileSystemKeepsRealDevices(string? device)
    {
        Assert.False(DfParser.IsPseudoFileSystem(device));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Filesystem 1024-blocks Used Available Capacity Mounted on")]
    public void ParseReturnsEmptyForUnusableInput(string? text)
    {
        Assert.Empty(DfParser.Parse(text));
    }
}