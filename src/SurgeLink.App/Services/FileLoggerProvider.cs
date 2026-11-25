using System.Text;
using System.Threading.Channels;
using SurgeLink.Core.Security;
using Microsoft.Extensions.Logging;

namespace SurgeLink.App.Services;

/// <summary>Minimal rolling text logger so users can attach diagnostics to bug reports.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int MaxRetainedDays = 14;
    private const int BatchSize = 128;
    private readonly string _directory;
    private readonly Channel<(DateOnly Day, string Text)> _pending = Channel.CreateBounded<(DateOnly, string)>(
        new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _writer;
    private DateOnly _lastPruneDate;

    /// <summary>Creates a provider writing one file per day under <paramref name="directory"/>.</summary>
    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        _writer = Task.Run(WriteQueuedAsync);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        _pending.Writer.TryComplete();
        _writer.GetAwaiter().GetResult();
    }

    private void Write(string line) => _pending.Writer.TryWrite((DateOnly.FromDateTime(DateTime.Now), line));

    private async Task WriteQueuedAsync()
    {
        var buffer = new StringBuilder();
        while (await _pending.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            DateOnly day = default;
            int remaining = BatchSize;
            while (remaining-- > 0 && _pending.Reader.TryRead(out var entry))
            {
                if (buffer.Length != 0 && day != entry.Day)
                {
                    await WriteBatchAsync(day, buffer.ToString()).ConfigureAwait(false);
                    buffer.Clear();
                }
                day = entry.Day;
                buffer.AppendLine(DiagnosticText.Escape(entry.Text));
            }
            if (buffer.Length != 0)
            {
                await WriteBatchAsync(day, buffer.ToString()).ConfigureAwait(false);
                buffer.Clear();
            }
        }
    }

    private async Task WriteBatchAsync(DateOnly day, string batch)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, $"surge-{day:yyyyMMdd}.log");
            if (_lastPruneDate != day)
            {
                _lastPruneDate = day;
                PruneOldLogs(day);
            }
            await File.AppendAllTextAsync(path, batch, Encoding.UTF8).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void PruneOldLogs(DateOnly today)
    {
        var cutoff = today.AddDays(-MaxRetainedDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "surge-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length != 14 || !DateOnly.TryParseExact(name.AsSpan(6), "yyyyMMdd", out var fileDate))
                continue;
            if (fileDate >= cutoff) continue;
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{Short(logLevel)}] {_category}: {message}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            _provider.Write(line);
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
