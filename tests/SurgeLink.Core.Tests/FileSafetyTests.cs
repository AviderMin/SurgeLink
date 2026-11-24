using System.Text;
using SurgeLink.Core.Files;

namespace SurgeLink.Core.Tests;

public sealed class FileSafetyTests
{
    [Fact]
    public void DownloadDirectoryCreatesMissingChildrenAndPinsAncestorsUntilDisposed()
    {
        using var temp = new TempDirectory();
        var destination = temp.PathOf("parent", "child");
        using (LocalDownloadPath.LockDirectory(destination))
        {
            Assert.True(Directory.Exists(destination));
            File.WriteAllText(Path.Combine(destination, "payload.txt"), "download");
            if (OperatingSystem.IsWindows())
                Assert.Throws<IOException>(() => Directory.Move(temp.PathOf("parent"), temp.PathOf("moved")));
        }
        Directory.Move(temp.PathOf("parent"), temp.PathOf("moved"));
        Assert.Equal("download", File.ReadAllText(temp.PathOf("moved", "child", "payload.txt")));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UploadSourceOpensExistingFilesWithoutCreatingDirectories(bool exists)
    {
        using var temp = new TempDirectory();
        var directory = temp.PathOf("source");
        var source = Path.Combine(directory, "payload.txt");
        if (!exists)
        {
            Assert.ThrowsAny<IOException>(() => LocalDownloadPath.OpenExistingFileForRead(source));
            Assert.False(Directory.Exists(directory));
            return;
        }

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(source, "upload");
        await using var stream = LocalDownloadPath.OpenExistingFileForRead(source);
        Assert.True(stream.IsAsync);
        using var reader = new StreamReader(stream);
        Assert.Equal("upload", await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WindowsUploadSourceRejectsReparsePoints(bool linkToDirectory)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var realDirectory = temp.PathOf("real");
        var link = temp.PathOf("link");
        Directory.CreateDirectory(realDirectory);
        var realFile = Path.Combine(realDirectory, "payload.txt");
        File.WriteAllText(realFile, "upload");
        try
        {
            if (linkToDirectory) Directory.CreateSymbolicLink(link, realDirectory);
            else File.CreateSymbolicLink(link, realFile);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == 1314)
        {
            return; // This Windows account cannot create symbolic links.
        }

        var source = linkToDirectory ? Path.Combine(link, "payload.txt") : link;
        Assert.Throws<IOException>(() => LocalDownloadPath.OpenExistingFileForRead(source));
    }

    [Theory]
    [InlineData("..\\outside.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("/etc/file")]
    [InlineData("file:stream")]
    [InlineData("a/b")]
    [InlineData("NUL.txt")]
    [InlineData("COM1")]
    [InlineData("LPT\u00b2.txt")]
    [InlineData("CON .txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("..")]
    public void UnsafeWindowsDownloadNamesAreRejected(string name)
    {
        using var temp = new TempDirectory();
        Assert.Throws<IOException>(() => LocalDownloadPath.Resolve(temp.Root, name));
    }

    [Fact]
    public void UnicodeNameStaysInsideDestination()
    {
        using var temp = new TempDirectory();
        Assert.Equal(temp.File("报告.txt"), LocalDownloadPath.Resolve(temp.Root, "报告.txt"));
    }

    [Fact]
    public async Task TextLimitUsesActualBytes()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("12345"));
        await Assert.ThrowsAsync<IOException>(() => BoundedTextFile.ReadAsync(stream, 4, CancellationToken.None));
    }

    [Fact]
    public async Task TextAtLimitIsAccepted()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("文本"));
        Assert.Equal("文本", await BoundedTextFile.ReadAsync(stream, 6, CancellationToken.None));
    }

    [Theory]
    [InlineData(new byte[] { 255, 255, 255 })]
    [InlineData(new byte[] { 65, 0, 66 })]
    public async Task InvalidOrBinaryTextIsRejected(byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes);
        var failure = await Record.ExceptionAsync(() => BoundedTextFile.ReadAsync(stream, 32, CancellationToken.None));
        Assert.True(failure is IOException or DecoderFallbackException);
    }

    [Fact]
    public async Task CancelledReadIsNotCompleted()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new MemoryStream(new byte[1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoundedTextFile.ReadAsync(stream, 4, cancellation.Token));
    }
    [Theory]
    [InlineData(65001, false)]
    [InlineData(65001, true)]
    [InlineData(1200, true)]
    [InlineData(1201, true)]
    [InlineData(12000, true)]
    [InlineData(12001, true)]
    public async Task EditingPreservesEncodingAndBom(int codePage, bool bom)
    {
        Encoding encoding = codePage switch
        {
            65001 => new UTF8Encoding(bom, true),
            1200 => new UnicodeEncoding(false, bom, true),
            1201 => new UnicodeEncoding(true, bom, true),
            12000 => new UTF32Encoding(false, bom, true),
            _ => new UTF32Encoding(true, bom, true),
        };
        byte[] original = [.. encoding.GetPreamble(), .. encoding.GetBytes("中文 😀\r\n")];
        using var stream = new MemoryStream(original);
        var document = await BoundedTextFile.ReadDocumentAsync(stream, 128, CancellationToken.None);
        Assert.Equal("中文 😀\r\n", document.Text);
        Assert.Equal(original, document.Encode(document.Text));
        Assert.Equal<byte>([.. encoding.GetPreamble(), .. encoding.GetBytes("修改")], document.Encode("修改"));
    }

    [Theory]
    [InlineData(new byte[] { 0xff, 0xfe, 0x41 })]
    [InlineData(new byte[] { 0, 0, 0xfe, 0xff, 0 })]
    public async Task MalformedBomEncodedTextIsRejected(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => BoundedTextFile.ReadDocumentAsync(stream, 32, CancellationToken.None));
    }

    [Fact]
    public async Task OpenExistingFileForReadNormalizesUnnormalizedAndExtendedPaths()
    {
        using var temp = new TempDirectory();
        var sub = Path.Combine(temp.Root, "sub");
        Directory.CreateDirectory(sub);
        var target = temp.File("payload.txt");
        await File.WriteAllTextAsync(target, "hello");

        var dotted = Path.Combine(sub, "..", "payload.txt");
        await using (var s1 = LocalDownloadPath.OpenExistingFileForRead(dotted))
        using (var r1 = new StreamReader(s1))
        {
            Assert.Equal("hello", await r1.ReadToEndAsync());
        }

        if (OperatingSystem.IsWindows())
        {
            await using var s2 = LocalDownloadPath.OpenExistingFileForRead(@"\\?\" + target);
            using var r2 = new StreamReader(s2);
            Assert.Equal("hello", await r2.ReadToEndAsync());
        }
    }
}
