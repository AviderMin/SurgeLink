using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace SurgeLink.Terminal.Tests;

[Collection(TerminalPerformanceGroup.Name)]
public sealed class PerformanceTests
{
    private readonly ITestOutputHelper _output;

    public PerformanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void OneMegabyteOfMixedAnsiIsFedQuickly()
    {
        const string line = "\u001b[32muser@host\u001b[0m:\u001b[1;34m~/source\u001b[0m$ ls -la --color=auto\r\n";
        var builder = new StringBuilder(1_100_000);
        while (builder.Length < 1_000_000)
        {
            builder.Append(line);
        }

        byte[] payload = Encoding.UTF8.GetBytes(builder.ToString());

        var warmup = new TerminalEmulator(120, 40, 10000);
        warmup.Feed(payload.AsSpan(0, 64 * 1024));

        var emulator = new TerminalEmulator(120, 40, 10000);
        var stopwatch = Stopwatch.StartNew();
        emulator.Feed(payload);
        stopwatch.Stop();

        _output.WriteLine(
            "1 MB mixed ANSI: {0:F1} ms, {1} scrollback lines, version {2}",
            stopwatch.Elapsed.TotalMilliseconds,
            emulator.ScrollbackLength,
            emulator.Version);

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(200),
            $"feeding 1 MB took {stopwatch.ElapsedMilliseconds} ms");
        Assert.True(emulator.ScrollbackLength > 0);
    }

    [Fact]
    public void SearchingLargeScrollbackStaysBounded()
    {
        const int lineCount = 100_000;
        var builder = new StringBuilder(lineCount * 48);
        for (int index = 0; index < lineCount; index++)
        {
            builder.Append("line ").Append(index).Append(" abcdefghijklmnopqrstuvwxyz0123456789\r\n");
        }

        var emulator = new TerminalEmulator(120, 40, lineCount);
        emulator.Feed(Encoding.UTF8.GetBytes(builder.ToString()));
        var stopwatch = Stopwatch.StartNew();
        var matches = emulator.FindText("needle");
        stopwatch.Stop();

        _output.WriteLine(
            "100k-line search: {0:F1} ms, {1} matches, {2} scrollback lines",
            stopwatch.Elapsed.TotalMilliseconds,
            matches.Count,
            emulator.ScrollbackLength);

        Assert.Empty(matches);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(1000),
            $"searching 100k lines took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void LongLineWithoutNewlinesIsHandled()
    {
        var emulator = new TerminalEmulator(80, 24, 1000);
        string text = new string('x', 1_000_000);
        byte[] payload = Encoding.UTF8.GetBytes(text);
        var stopwatch = Stopwatch.StartNew();
        emulator.Feed(payload);
        stopwatch.Stop();

        _output.WriteLine(
            "1 MB single line: {0:F1} ms, {1} scrollback lines",
            stopwatch.Elapsed.TotalMilliseconds,
            emulator.ScrollbackLength);

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(200),
            $"wrapped long line took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(1000, emulator.ScrollbackLength);
    }
}
