using System.Globalization;
using SurgeLink.Core.Storage;
using SurgeLink.Core.Security;

namespace SurgeLink.App.Services;

/// <summary>
/// Last-resort crash recorder. WinUI tears the process down when an exception
/// escapes a UI callback, so the exception is written to disk before that
/// happens; the file lands next to the normal log files.
/// </summary>
internal static class CrashLog
{
    private static readonly object Gate = new();

    /// <summary>Writes the exception and returns the file it went to (best effort).</summary>
    public static string? Write(string source, Exception? exception)
    {
        try
        {
            var now = DateTime.Now;
            var path = Path.Combine(AppPaths.LogsDirectory, "crash-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            var timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
            var text = $"{timestamp} [{DiagnosticText.Escape(source)}]{Environment.NewLine}{DiagnosticText.Escape(exception?.ToString() ?? string.Empty)}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(path, text);
            }

            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
