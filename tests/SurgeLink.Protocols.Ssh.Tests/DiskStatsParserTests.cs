using SurgeLink.Protocols.Ssh.Monitoring;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class DiskStatsParserTests
{
    private const string Sample = """
           8       0 sda 12345 678 987654 12345 67890 123 456789 23456 0 34567 45678
           8       1 sda1 1234 56 78901 1234 5678 12 45678 2345 0 3456 4567
         259       0 nvme0n1 100 0 2000 30 400 0 5000 60 1 70 80
        """;

    [Fact]
    public void ParseReadsDeviceCounters()
    {
        var stats = DiskStatsParser.Parse(Sample);

        Assert.Equal(3, stats.Count);
        var disk = stats[0];
        Assert.Equal("sda", disk.DeviceName);
        Assert.Equal(12345, disk.ReadsCompleted);
        Assert.Equal(987654, disk.SectorsRead);
        Assert.Equal(67890, disk.WritesCompleted);
        Assert.Equal(456789, disk.SectorsWritten);
        Assert.Equal(0, disk.IosInProgress);
        Assert.Equal(34567, disk.IoMilliseconds);
    }

    [Fact]
    public void SumWholeDeviceSectorsSkipsPartitionsAndPseudoDevices()
    {
        const string text = """
               8       0 sda 12345 678 987654 12345 67890 123 456789 23456 0 34567 45678
               8       1 sda1 1234 56 78901 1234 5678 12 45678 2345 0 3456 4567
             259       0 nvme0n1 100 0 2000 30 400 0 5000 60 1 70 80
             259       1 nvme0n1p1 10 0 200 3 40 0 500 6 0 7 8
               7       0 loop0 1 0 2 3 4 0 5 6 0 7 8
            """;

        var stats = DiskStatsParser.Parse(text);
        var (read, write) = DiskStatsParser.SumWholeDeviceSectors(stats);

        Assert.Equal(987654 + 2000, read);
        Assert.Equal(456789 + 5000, write);
    }

    [Fact]
    public void SumWholeDeviceSectorsSkipsStackedLogicalDevices()
    {
        const string text = """
               8       0 sda 100 0 1000 0 200 0 2000 0 0 0 0
               8       1 sda1 90 0 900 0 180 0 1800 0 0 0 0
             253       0 dm-0 100 0 1000 0 200 0 2000 0 0 0 0
               9       0 md0 100 0 1000 0 200 0 2000 0 0 0 0
            """;

        var stats = DiskStatsParser.Parse(text);
        var (read, write) = DiskStatsParser.SumWholeDeviceSectors(stats);

        Assert.Equal(1000, read);
        Assert.Equal(2000, write);
    }

    [Theory]
    [InlineData("sda", true)]
    [InlineData("sdb", true)]
    [InlineData("sdm", true)]
    [InlineData("hda", true)]
    [InlineData("vda", true)]
    [InlineData("xvda", true)]
    [InlineData("nvme0n1", true)]
    [InlineData("mmcblk0", true)]
    [InlineData("sda1", false)]
    [InlineData("nvme0n1p1", false)]
    [InlineData("mmcblk0p1", false)]
    [InlineData("dm-0", false)]
    [InlineData("md0", false)]
    [InlineData("md127", false)]
    [InlineData("md_d0", false)]
    [InlineData("loop0", false)]
    [InlineData("ram0", false)]
    [InlineData("zram0", false)]
    [InlineData("sr0", false)]
    [InlineData("", false)]
    public void IsWholeDeviceClassifiesKernelDeviceNames(string name, bool expected)
    {
        Assert.Equal(expected, DiskStatsParser.IsWholeDevice(name));
    }

    [Fact]
    public void ParseSkipsShortAndMalformedLines()
    {
        const string text = """
               8       0 sda 12345 678 987654 12345 67890 123 456789 23456 0 34567 45678
              this line is not a diskstats row
               8       1 short 1 2 3
               8       2 malformed x 1 2 3 4 5 6 7 8 9 10
            """;

        var stats = DiskStatsParser.Parse(text);

        Assert.Single(stats);
        Assert.Equal("sda", stats[0].DeviceName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   8       0 sda")] 
    public void ParseReturnsEmptyForUnusableInput(string? text)
    {
        Assert.Empty(DiskStatsParser.Parse(text));
    }
}
