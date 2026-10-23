using SurgeLink.Core.Sessions;
using SurgeLink.Core.Storage;

namespace SurgeLink.Core.Tests;

public sealed class AtomicJsonFileTests
{
    [Fact]
    public void WriteThenReadRoundTrips()
    {
        using var temp = new TempDirectory();
        string path = temp.File("values.json");
        var values = new List<string> { "alpha", "beta" };

        AtomicJsonFile.Write(path, values);

        List<string> read = AtomicJsonFile.Read(path, new List<string>());
        Assert.Equal(values, read);
    }

    [Fact]
    public void ReadMissingFileReturnsFallback()
    {
        using var temp = new TempDirectory();
        var fallback = new List<string> { "fallback" };

        List<string> read = AtomicJsonFile.Read(temp.File("missing.json"), fallback);

        Assert.Same(fallback, read);
    }

    [Fact]
    public void ReadCorruptFileReturnsFallback()
    {
        using var temp = new TempDirectory();
        string path = temp.File("corrupt.json");
        File.WriteAllText(path, "{ this is not json");

        List<string> read = AtomicJsonFile.Read(path, new List<string>());

        Assert.Empty(read);
    }

    [Fact]
    public void ReadEmptyFileReturnsFallback()
    {
        using var temp = new TempDirectory();
        string path = temp.File("empty.json");
        File.WriteAllText(path, string.Empty);

        List<string> read = AtomicJsonFile.Read(path, new List<string>());

        Assert.Empty(read);
    }

    [Fact]
    public void ReadJsonNullReturnsFallback()
    {
        using var temp = new TempDirectory();
        string path = temp.File("null.json");
        File.WriteAllText(path, "null");

        List<string> read = AtomicJsonFile.Read(path, new List<string>());

        Assert.Empty(read);
    }

    [Fact]
    public void ReadWrongShapeReturnsFallback()
    {
        using var temp = new TempDirectory();
        string path = temp.File("wrong-shape.json");
        File.WriteAllText(path, "{\"unexpected\":42}");

        List<string> read = AtomicJsonFile.Read(path, new List<string>());

        Assert.Empty(read);
    }

    [Fact]
    public void ReadUnsupportedTypeReturnsFallback()
    {
        using var temp = new TempDirectory();
        string path = temp.File("unsupported.json");
        File.WriteAllText(path, "\"System.String\"");
        var fallback = typeof(string);

        Type read = AtomicJsonFile.Read(path, fallback);

        Assert.Same(fallback, read);
    }

    [Fact]
    public void WriteCreatesMissingDirectories()
    {
        using var temp = new TempDirectory();
        string path = temp.PathOf("nested", "deeper", "value.json");

        AtomicJsonFile.Write(path, "payload");

        Assert.True(File.Exists(path));
        Assert.Equal("payload", AtomicJsonFile.Read(path, "missing"));
    }

    [Fact]
    public void WriteReplacesExistingContent()
    {
        using var temp = new TempDirectory();
        string path = temp.File("replace.json");

        AtomicJsonFile.Write(path, "first");
        AtomicJsonFile.Write(path, "second");

        Assert.Equal("second", AtomicJsonFile.Read(path, "missing"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void WriteUsesWebJsonConventions()
    {
        using var temp = new TempDirectory();
        string path = temp.File("profile.json");
        var profile = new SessionProfile
        {
            Id = "p1",
            Name = "Session",
            ProtocolId = "ssh",
            Host = "example.test",
            Username = "alice",
        };

        AtomicJsonFile.Write(path, profile);
        string json = File.ReadAllText(path);

        Assert.Contains("\"id\": \"p1\"", json);
        Assert.Contains("\"protocolId\": \"ssh\"", json);
        Assert.DoesNotContain("\"Id\"", json);
        Assert.Contains("\n", json);
    }
}
