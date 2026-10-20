using System.Globalization;
using SurgeLink.App.Localization;

namespace SurgeLink.App.Components;

/// <summary>
/// Human readable formatting shared by the data views: byte sizes, transfer
/// rates, durations and timestamps. Keeping the units in one place makes the
/// SFTP browser and the performance dashboard read consistently.
/// </summary>
internal static class DataFormat
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Formats a byte count with 1024 based units, e.g. <c>1.4 MB</c>.</summary>
    public static string Bytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var value = (double)bytes;
        var unit = 0;

        // Compare the rounded value so 1023.6 KB reads as 1.0 MB instead of "1024 KB".
        while (Math.Round(value) >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 || value >= 100 ? "0" : "0.0";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + ByteUnits[unit];
    }

    /// <summary>Formats a byte rate, e.g. <c>2.4 MB/s</c>; negative values mean "not available".</summary>
    public static string Rate(double bytesPerSecond) =>
        bytesPerSecond < 0
            ? Str.T("monitor.na", "n/a")
            : Bytes((long)bytesPerSecond) + "/s";

    /// <summary>Formats a 0..1 fraction as a percentage, e.g. <c>63%</c>.</summary>
    public static string Percent(double fraction) =>
        (Math.Clamp(fraction, 0d, 1d) * 100).ToString("0", CultureInfo.CurrentCulture) + "%";

    /// <summary>Formats a 0..100 value with one decimal, e.g. <c>12.4%</c>.</summary>
    public static string PercentValue(double percent) =>
        percent.ToString("0.0", CultureInfo.CurrentCulture) + "%";

    /// <summary>Formats an uptime as <c>12d 0h 43m</c>, <c>3h 12m</c> or <c>45s</c>.</summary>
    public static string Duration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return Str.T("monitor.na", "n/a");
        }

        if (value.TotalDays >= 1)
        {
            return Str.F("monitor.duration.days", "{0}d {1}h {2}m",
                (int)value.TotalDays, value.Hours, value.Minutes);
        }

        if (value.TotalHours >= 1)
        {
            return Str.F("monitor.duration.hours", "{0}h {1}m", value.Hours, value.Minutes);
        }

        if (value.TotalMinutes >= 1)
        {
            return Str.F("monitor.duration.minutes", "{0}m {1}s", value.Minutes, value.Seconds);
        }

        return Str.F("monitor.duration.seconds", "{0}s", value.Seconds);
    }

    /// <summary>Formats a sample timestamp as local wall clock time, e.g. <c>14:02:11</c>.</summary>
    public static string Clock(DateTimeOffset value) => value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>Formats a count with thousands separators.</summary>
    public static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Formats a Unix mode as octal, e.g. <c>644</c>.</summary>
    public static string Octal(int mode) => Convert.ToString(mode & 0xFFF, 8).PadLeft(3, '0');
}
