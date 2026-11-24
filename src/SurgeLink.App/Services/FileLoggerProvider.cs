using System.Text;
using SurgeLink.Core.Security;
using Microsoft.Extensions.Logging;

namespace SurgeLink.App.Services;

/// <summary>Minimal rolling text logger so users can attach diagnostics to bug reports.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int MaxRetainedDays = 14;
    private readonly string _directory;
    private readonly object _gate = new();
    private DateOnly _lastPruneDate;

    /// <summary>Creates a provider writing one file per day under <paramref name="directory"/>.</summary>
    public FileLoggerProvider(string directory) => _directory = directory;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            var path = Path.Combine(_directory, $"surge-{now:yyyyMMdd}.log");
            lock (_gate)
            {
                if (_lastPruneDate != today)
                {
                    _lastPruneDate = today;
                    PruneOldLogs(today);
                }
                File.AppendAllText(path, DiagnosticText.Escape(line) + Environment.NewLine, Encoding.UTF8);
            }
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
