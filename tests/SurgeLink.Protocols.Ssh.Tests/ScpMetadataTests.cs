using System.Diagnostics;
using System.Text;
using SurgeLink.Core.Files;
using SurgeLink.Core.Protocols;
using SurgeLink.Protocols.Ssh.Internal;
using Xunit;

namespace SurgeLink.Protocols.Ssh.Tests;

public sealed class ScpMetadataTests
{
    private const string AttackName = "ghost\n-rw-r--r-- 1 root root 1 Oct 25 12:00 victim";

    [Theory]
    [InlineData(AttackName)]
    [InlineData(" leading and trailing ")]
    [InlineData("..\\victim.txt")]
    [InlineData("a -> different")]
    [InlineData("-dash\t控制\r\nfile")]
    public void ListingPreservesTheExactIdentity(string name)
    {
        var entry = Assert.Single(ScpMetadata.Parse("/data", Packet(Record("/data/" + name)), true));
        Assert.Equal(name, entry.Name);
        Assert.Equal("/data/" + name, entry.FullPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListAndStatKeepAttackNameSeparateFromTheVictim(bool directory)
    {
        var path = "/data/" + AttackName;
        var packet = directory ? Packet(Record(path), Record("/data/victim")) : Packet(Record(path));
        var entries = ScpMetadata.Parse(directory ? "/data" : path, packet, directory);
        Assert.Equal(directory ? 2 : 1, entries.Count);
        Assert.Equal(path, entries[0].FullPath);
        Assert.DoesNotContain(entries, entry => entry.FullPath == "/data/ghost");
    }

    [Theory]
    [InlineData(0x89A4)]
    [InlineData(0x85A4)]
    [InlineData(0x83A4)]
    [InlineData(0x8FED)]
    public void MetadataPreservesSpecialPermissions(int mode) => Assert.Equal(mode & 0xFFF,
        Assert.Single(ScpMetadata.Parse("/data", Packet(Record("/data/file", mode)), true)).UnixMode);

    [Fact]
    public void MetadataUsesExactUnixUtcTimeAndNumericOwners()
    {
        var entry = Assert.Single(ScpMetadata.Parse("/data", Packet(Record("/data/file", modified: 1767182400)), true));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767182400), entry.ModifiedUtc);
        Assert.Equal(TimeSpan.Zero, entry.ModifiedUtc.Offset);
        Assert.Equal("1000", entry.Owner);
        Assert.Equal("1001", entry.Group);
    }

    [Fact]
    public void LinkNameAndTargetAreIndependentFields()
    {
        var target = "../target\nwith -> arrows\n";
        var entry = Assert.Single(ScpMetadata.Parse("/data", Packet(Record("/data/link -> name", 0xA1FF, target)), true));
        Assert.Equal(RemoteEntryKind.SymbolicLink, entry.Kind);
        Assert.Equal("link -> name", entry.Name);
        Assert.Equal(target, entry.LinkTarget);
    }

    [Fact]
    public void RootAndEmptyDirectoriesHaveAnExplicitFrame()
    {
        Assert.Empty(ScpMetadata.Parse("/data", Packet(), true));
        var root = Assert.Single(ScpMetadata.Parse("/", Packet(Record("/", 0x41ED)), false));
        Assert.Equal("/", root.Name);
        Assert.Equal(RemoteEntryKind.Directory, root.Kind);
        Assert.Equal(0, root.Size);
    }

    [Theory]
    [InlineData("/outside/file")]
    [InlineData("/data/../victim")]
    [InlineData("/data/.")]
    [InlineData("/data/nested/file")]
    [InlineData("/data/")]
    public void ListingRejectsRedirectedIdentities(string path) =>
        Assert.Throws<ProtocolException>(() => ScpMetadata.Parse("/data", Packet(Record(path)), true));

    [Theory]
    [InlineData("truncated")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("invalid-utf8")]
    [InlineData("nul")]
    [InlineData("plain-ls")]
    public void InvalidResponsesRejectTheWholeListing(string scenario)
    {
        var record = Record("/data/valid");
        var output = scenario switch
        {
            "truncated" => "SURGELINK-FS-1\0" + record,
            "malformed" => Packet(record, "not-metadata\0" + Hex("/data/file") + "\0\0"),
            "duplicate" => Packet(record, record),
            "invalid-utf8" => Packet(record, "81a4 1 0 0 0\0FF\0\0"),
            "nul" => Packet(record, Record("/data/a\0b")),
            _ => "-rw-r--r-- 1 root root 1 Oct 25 12:00 ghost\n-rw-r--r-- 1 root root 1 Oct 25 12:00 victim\n",
        };
        Assert.Throws<ProtocolException>(() => ScpMetadata.Parse("/data", output, true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("81a4 -1 0 0 0")]
    [InlineData("81a4 1 999999999999999 0 0")]
    [InlineData("xyz 1 0 0 0")]
    [InlineData("1FFFF 1 0 0 0")]
    [InlineData("0000 1 0 0 0")]
    public void InvalidMetadataIsRejected(string metadata) =>
        Assert.Throws<ProtocolException>(() => ScpMetadata.Parse("/data", Packet(metadata + "\0" + Hex("/data/file") + "\0\0"), true));

    [Fact]
    public void StatRequiresExactlyTheRequestedIdentity()
    {
        Assert.Throws<ProtocolException>(() => ScpMetadata.Parse("/data/file", Packet(), false));
        Assert.Throws<ProtocolException>(() => ScpMetadata.Parse("/data/file", Packet(Record("/data/other")), false));
        Assert.Throws<ProtocolException>(() => ScpMetadata.Parse("/data/file", Packet(Record("/data/file"), Record("/data/other")), false));
    }

    [Fact]
    public void HomePathKeepsTrailingCrAndNewlineAndRejectsLossyDecoding()
    {
        const string path = "/home/user\r\n\n";
        Assert.Equal(path, ScpMetadata.ParseHome("SURGELINK-PWD-1\0" + Hex(path) + "\0END\0"));
        Assert.Throws<ProtocolException>(() => ScpMetadata.ParseHome("SURGELINK-PWD-1\0FF\0END\0"));
        Assert.Throws<ProtocolException>(() => ScpMetadata.ParseHome("/home/user\n"));
    }

    [PosixShellFact]
    public async Task ActualShellHomeProtocolReturnsAnAbsolutePath()
    {
        var (code, output, error) = await ExecuteAsync(ScpMetadata.BuildHomeCommand());
        Assert.True(code == 0, error);
        Assert.StartsWith("/", ScpMetadata.ParseHome(output));
    }

    [Theory]
    [InlineData("/data/..\\victim.txt")]
    [InlineData("/data/report.txt ")]
    [InlineData("/data/link/../target")]
    public void CommandPathsKeepTheirExactPosixMeaning(string path) => Assert.Equal(path, ScpFileSystem.NormalizePath(path));

    [PosixShellFact]
    public async Task ActualShellPreservesNewlineFileIdentityForListAndStat()
    {
        var root = "/tmp/surge-scp-metadata-" + Guid.NewGuid().ToString("N");
        var attack = root + "/" + AttackName;
        var unusual = root + "/quote' 控制\t ends \n";
        var cleanup = "rm -f -- " + Quote(attack) + " " + Quote(unusual) + " " + Quote(root + "/victim") + "; rmdir -- " + Quote(root);
        var setup = "mkdir -- " + Quote(root) + " || exit 1\ntrap " + Quote(cleanup) + " EXIT\n"
            + "printf attack > " + Quote(attack) + "\nprintf other > " + Quote(unusual) + "\nprintf keep > " + Quote(root + "/victim") + "\n";
        foreach (var directory in new[] { true, false })
        {
            var path = directory ? root : attack;
            var (code, output, error) = await ExecuteAsync(setup + ScpMetadata.BuildCommand(path, directory)
                + "\nrc=$?\n[ \"$(cat -- " + Quote(root + "/victim") + ")\" = keep ] || exit 1\nexit \"$rc\"");
            Assert.True(code == 0, error);
            var entries = ScpMetadata.Parse(path, output, directory);
            Assert.Equal(directory ? 3 : 1, entries.Count);
            Assert.Contains(entries, entry => entry.Name == AttackName && entry.FullPath == attack);
            if (directory) { Assert.Contains(entries, entry => entry.FullPath == unusual); }
            Assert.DoesNotContain(entries, entry => entry.FullPath == root + "/ghost");
        }
    }

    [PosixShellFact]
    public async Task UnsupportedStatFailsWithoutReturningLsEntries()
    {
        var command = ScpMetadata.BuildCommand("/", false).Replace("export LC_ALL=C", "export LC_ALL=C\nstat() { return 1; }", StringComparison.Ordinal);
        var (code, output, _) = await ExecuteAsync(command);
        Assert.NotEqual(0, code);
        Assert.Throws<ProtocolException>(() => ScpMetadata.Parse("/", output, false));
    }

    private static string Record(string path, int mode = 0x81A4, string target = "", long modified = 0) =>
        mode.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + " 1 " + modified.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " 1000 1001\0" + Hex(path) + "\0" + Hex(target) + "\0";
    private static string Packet(params string[] records) => "SURGELINK-FS-1\0" + string.Concat(records) + "END\0";
    private static string Hex(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static async Task<(int Code, string Output, string Error)> ExecuteAsync(string command)
    {
        var start = new ProcessStartInfo(PosixShellFactAttribute.Shell)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add(command);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        return (process.ExitCode, await output, await error);
    }
}
