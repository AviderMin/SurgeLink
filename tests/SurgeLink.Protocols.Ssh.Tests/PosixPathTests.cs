using SurgeLink.Protocols.Ssh;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class PosixPathTests
{
    [Theory]
    [InlineData("/a/b/../c", null, "/a/c")]
    [InlineData("/a/./b//c/", null, "/a/b/c")]
    [InlineData("a/b", "/home/alice", "/home/alice/a/b")]
    [InlineData("a//b/./c/", "/home/alice", "/home/alice/a/b/c")]
    [InlineData("../../..", "/home/alice", "/")]
    [InlineData("..", "/", "/")]
    [InlineData("", null, "/")]
    [InlineData("   ", null, "/   ")]
    [InlineData("/", null, "/")]
    [InlineData("a\\b", "/home/alice", "/home/alice/a\\b")]
    public void NormalizeResolvesRelativePosixPaths(string? path, string? currentDirectory, string expected)
    {
        Assert.Equal(expected, PosixPath.Normalize(path, currentDirectory));
    }

    [Fact]
    public void NormalizeAcceptsNull()
    {
        Assert.Equal("/", PosixPath.Normalize(null));
    }

    [Theory]
    [InlineData("/data/report.txt ", "/data/report.txt ")]
    [InlineData("/data/..\\victim.txt", "/data/..\\victim.txt")]
    [InlineData("/data/link/../target", "/data/link/../target")]
    [InlineData(" leading name ", "/home/alice/ leading name ")]
    [InlineData("~/file ", "/home/alice/file ")]
    public void ResolvePreservesLiteralNamesAndServerSideSymlinkTraversal(string path, string expected)
    {
        Assert.Equal(expected, PosixPath.Resolve(path, "/home/alice", "/home/alice"));
    }

    [Fact]
    public void ResolveRejectsNulInsteadOfChangingTheTarget()
    {
        Assert.Throws<ArgumentException>(() => PosixPath.Resolve("/safe\0/other"));
    }

    [Fact]
    public void RootHomeExpansionRemainsRootEvenWithADifferentCurrentDirectory()
    {
        Assert.Equal("/", PosixPath.Resolve("~", "/current", "/"));
        Assert.Equal("/file", PosixPath.Resolve("~/file", "/current", "/"));
    }

    [Theory]
    [InlineData("~/documents", "/home/alice/documents")]
    [InlineData("~", "/home/alice")]
    [InlineData("~alice/file.txt", "/other/~alice/file.txt")]
    public void NormalizeExpandsTildeWhenTheHomeDirectoryIsKnown(string path, string expected)
    {
        Assert.Equal(expected, PosixPath.Normalize(path, "/other", "/home/alice"));
    }

    [Fact]
    public void NormalizeKeepsTildeLiteralWithoutAHomeDirectory()
    {
        Assert.Equal("/~", PosixPath.Normalize("~"));
    }

    [Theory]
    [InlineData("/home/alice", "stuff", "/home/alice/stuff")]
    [InlineData("/home/alice", "/absolute/path", "/absolute/path")]
    [InlineData("/home/alice", "..", "/home")]
    [InlineData("/home/alice", "", "/home/alice")]
    [InlineData(null, "relative", "/relative")]
    public void CombineJoinsOrReplacesTheBaseDirectory(string? baseDirectory, string path, string expected)
    {
        Assert.Equal(expected, PosixPath.Combine(baseDirectory, path));
    }

    [Theory]
    [InlineData("/home/alice/file.txt", "file.txt")]
    [InlineData("/home/alice/", "alice")]
    [InlineData("/", "/")]
    [InlineData(null, "/")]
    public void GetNameReturnsTheLastSegment(string? path, string expected)
    {
        Assert.Equal(expected, PosixPath.GetName(path));
    }
}
