using Microsoft.Extensions.Logging;
using SurgeLink.App.Services;

namespace SurgeLink.Core.Tests;

public sealed class FileLoggerProviderTests
{
    [Fact]
    public void DisposeFlushesQueuedEntriesInOrder()
    {
        using var temp = new TempDirectory();
        var path = temp.File("logs");
        using (var provider = new FileLoggerProvider(path))
        {
            var logger = provider.CreateLogger("Performance");
            for (int i = 0; i < 300; i++) { logger.LogInformation("row {Row}", i); }
        }

        var entries = Directory.GetFiles(path, "surge-*.log").Order(StringComparer.Ordinal)
            .SelectMany(File.ReadLines).ToArray();
        Assert.Equal(300, entries.Length);
        for (int i = 0; i < entries.Length; i++) { Assert.EndsWith($"[INF] Performance: row {i}", entries[i]); }
    }
}
