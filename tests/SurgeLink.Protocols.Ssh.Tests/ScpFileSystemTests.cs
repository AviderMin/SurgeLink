using SurgeLink.Core.Files;
using SurgeLink.Protocols.Ssh;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class ScpFileSystemTests
{
    [Theory]
    [InlineData("rwSr--r--", 0x9A4)]
    [InlineData("rw-r-Sr--", 0x5A4)]
    [InlineData("rw-r--r-T", 0x3A4)]
    [InlineData("rwsr-sr-t", 0xFED)]
    public void SpecialPermissionsDoNotInventExecuteBits(string permissions, int expected)
    {
        var entry = ScpFileSystem.ParseEntry("/data", $"-{permissions} 1 root root 12 Jan 1 2026 file");
        Assert.NotNull(entry);
        Assert.Equal(expected, entry.UnixMode);
    }

    [Theory]
    [InlineData("/data/..\\victim.txt")]
    [InlineData("/data/report.txt ")]
    [InlineData("/data/link/../target")]
    public void CommandPathsKeepTheirExactPosixMeaning(string path) => Assert.Equal(path, ScpFileSystem.NormalizePath(path));

    [Fact]
    public void ParseEntryReadsBusyBoxDirectoryLine()
    {
        var entry = ScpFileSystem.ParseEntry(
            "/root",
            "drwxr-xr-x    2 root     root          4096 Oct 21 12:00 docs");

        Assert.NotNull(entry);
        Assert.Equal("docs", entry!.Name);
        Assert.Equal("/root/docs", entry.FullPath);
        Assert.Equal(RemoteEntryKind.Directory, entry.Kind);
        Assert.Equal(0, entry.Size);
        Assert.Equal(0b111_101_101, entry.UnixMode);
        Assert.Equal("root", entry.Owner);
        Assert.Equal("root", entry.Group);
    }

    [Fact]
    public void ParseEntryKeepsSpacesInNames()
    {
        var entry = ScpFileSystem.ParseEntry(
            "/root",
            "-rw-r--r--    1 root     root           123 Oct 21 12:00 notes for today.txt");

        Assert.NotNull(entry);
        Assert.Equal("notes for today.txt", entry!.Name);
        Assert.Equal("/root/notes for today.txt", entry.FullPath);
        Assert.Equal(123, entry.Size);
        Assert.Equal(0b110_100_100, entry.UnixMode);
    }

    [Fact]
    public void ParseEntrySplitsSymbolicLinkTarget()
    {
        var entry = ScpFileSystem.ParseEntry(
            "/",
            "lrwxrwxrwx    1 root     root             7 Oct 21 12:00 bin -> /usr/bin");

        Assert.NotNull(entry);
        Assert.Equal("bin", entry!.Name);
        Assert.Equal("/bin", entry.FullPath);
        Assert.Equal(RemoteEntryKind.SymbolicLink, entry.Kind);
        Assert.Equal("/usr/bin", entry.LinkTarget);
    }

    [Theory]
    [InlineData("total 12")]
    [InlineData("not a listing")]
    public void ParseEntryIgnoresNonEntries(string line)
    {
        Assert.Null(ScpFileSystem.ParseEntry("/", line));
    }
}
