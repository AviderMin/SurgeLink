using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit.Abstractions;

namespace SurgeLink.Terminal.Tests;

[Collection(TerminalPerformanceGroup.Name)]
public sealed class PerformanceTests
{
    private readonly ITestOutputHelper _output;

    public PerformanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void MixedUnicodeOutputAndRepeatedShortLineReflowStayWithinAllocationBudget()
    {
        Assert.Equal(4, Unsafe.SizeOf<TerminalColor>());
        Assert.InRange(Unsafe.SizeOf<TerminalCell>(), 16, 24);
        byte[] line = Encoding.UTF8.GetBytes("\u001b[32m主机中文进程\u001b[0m ── 0123456789 abcdefghijklmnopq\r\n");
        var input = new byte[line.Length * (10_000_000 / line.Length)];
        for (int offset = 0; offset < input.Length; offset += line.Length)
            line.CopyTo(input, offset);
        new TerminalEmulator().Feed(input.AsSpan(0, 64 * 1024));

        var terminal = new TerminalEmulator(120, 40, 10_000) { ReflowOnResize = true };
        long start = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        terminal.Feed(input);
        double feedMs = watch.Elapsed.TotalMilliseconds;
        long feedBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        for (int pass = 0; pass < 10; pass++) { terminal.Resize(120 - (pass % 2), 40); }
        double reflowMs = watch.Elapsed.TotalMilliseconds;
        long reflowBytes = GC.GetAllocatedBytesForCurrentThread() - start;

        _output.WriteLine("10 MB mixed ANSI/Unicode {0:F1} ms and {1:N0} B, 10 resize calls {2:F1} ms and {3:N0} B",
            feedMs, feedBytes, reflowMs, reflowBytes);
        Assert.Equal(10_000, terminal.ScrollbackLength);
        Assert.InRange(feedBytes, 0, 45_000_000);
        Assert.InRange(reflowBytes, 0, 3_000_000);
    }
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

        // A single wall-clock sample on a shared CI runner is too noisy: JIT,
        // antivirus scans and neighbouring jobs can add tens of milliseconds.
        // Measure three cold emulators and keep the best run; the budget still
        // rejects a real throughput regression.
        var bestMilliseconds = double.MaxValue;
        var scrollback = 0;
        for (var sample = 0; sample < 3; sample++)
        {
            var emulator = new TerminalEmulator(120, 40, 10000);
            var stopwatch = Stopwatch.StartNew();
            emulator.Feed(payload);
            stopwatch.Stop();
            bestMilliseconds = Math.Min(bestMilliseconds, stopwatch.Elapsed.TotalMilliseconds);
            scrollback = emulator.ScrollbackLength;
        }

        _output.WriteLine(
            "1 MB mixed ANSI best of 3: {0:F1} ms, {1} scrollback lines",
            bestMilliseconds,
            scrollback);

        Assert.True(
            bestMilliseconds < 200,
            $"feeding 1 MB took {bestMilliseconds:F0} ms (best of 3)");
        Assert.True(scrollback > 0);
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
