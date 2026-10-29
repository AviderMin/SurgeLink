using System.Text;
using SurgeLink.Core.Files;
using SurgeLink.Protocols.Demo;

namespace SurgeLink.Core.Tests;

public sealed class TextLineEndingsTests
{
    [Theory]
    [InlineData("a\r\nb\r\nc", "\r\n")]
    [InlineData("a\nb\nc", "\n")]
    [InlineData("a\rb\rc", "\r")]
    [InlineData("no breaks", "\n")]
    [InlineData("", "\n")]
    [InlineData("a\r\nb\nc", "\r\n")] // a CRLF/LF tie keeps CRLF
    [InlineData("a\rb\nc", "\n")]        // LF wins over a lone CR
    public void DetectFindsTheDominantConvention(string text, string expected) =>
        Assert.Equal(expected, TextLineEndings.Detect(text));

    [Theory]
    [InlineData("a\nb", "\r\n", "a\r\nb")]
    [InlineData("a\r\nb", "\n", "a\nb")]
    [InlineData("a\r\nb", "\r\n", "a\r\nb")]
    [InlineData("a\rb", "\r", "a\rb")]
    [InlineData("a\nb", "\n", "a\nb")]
    [InlineData("", "\r\n", "")]
    public void ApplyRestoresTheRequestedConvention(string text, string newLine, string expected) =>
        Assert.Equal(expected, TextLineEndings.Apply(text, newLine));

    [Fact]
    public void ApplyIsIdempotentForItsOwnConvention()
    {
        const string text = "first\r\nsecond\r\nthird";
        Assert.Equal(text, TextLineEndings.Apply(TextLineEndings.Apply(text, "\r\n"), "\r\n"));
    }

    [Fact]
    public async Task EditingACrlfFileKeepsCrlfLineEndingsOnSave()
    {
        var files = new DemoFileSystem();
        const string path = "/home/demo/crlf.txt";
        var original = Encoding.UTF8.GetBytes("alpha\r\nbeta\r\ngamma\r\n");
        await using (var output = await files.OpenWriteAsync(path, original.Length, null, default))
        {
            await output.WriteAsync(original);
        }

        var document = await ReadAsync(files, path);
        Assert.Equal("\r\n", TextLineEndings.Detect(document.Text));

        // The editor works on canonical LF text and re-applies the detected convention on save.
        var canonical = EditorTextBuffer.ToDocumentText(document.Text);
        var edited = canonical.Replace("beta", "BETA", StringComparison.Ordinal);
        var persisted = TextLineEndings.Apply(edited, TextLineEndings.Detect(document.Text));
        await RemoteTextEdit.SaveAsync(files, path, document, persisted, 1024, default);

        var saved = await ReadRawAsync(files, path);
        Assert.Equal("alpha\r\nBETA\r\ngamma\r\n", Encoding.UTF8.GetString(saved));
        Assert.DoesNotContain("\n", Encoding.UTF8.GetString(saved).Replace("\r\n", string.Empty));
    }

    [Fact]
    public async Task EditingAnLfFileDoesNotGainCrlfOnSave()
    {
        var files = new DemoFileSystem();
        const string path = "/home/demo/lf.txt";
        var original = Encoding.UTF8.GetBytes("alpha\nbeta\ngamma\n");
        await using (var output = await files.OpenWriteAsync(path, original.Length, null, default))
        {
            await output.WriteAsync(original);
        }

        var document = await ReadAsync(files, path);
        Assert.Equal("\n", TextLineEndings.Detect(document.Text));
        var canonical = EditorTextBuffer.ToDocumentText(document.Text);
        var persisted = TextLineEndings.Apply(canonical.Replace("beta", "BETA", StringComparison.Ordinal), "\n");
        await RemoteTextEdit.SaveAsync(files, path, document, persisted, 1024, default);

        Assert.Equal("alpha\nBETA\ngamma\n", Encoding.UTF8.GetString(await ReadRawAsync(files, path)));
    }

    private static async Task<TextFileDocument> ReadAsync(IRemoteFileSystem files, string path)
    {
        await using var input = await files.OpenReadAsync(path, default);
        return await BoundedTextFile.ReadDocumentAsync(input, 1024, default);
    }

    private static async Task<byte[]> ReadRawAsync(IRemoteFileSystem files, string path)
    {
        await using var input = await files.OpenReadAsync(path, default);
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
