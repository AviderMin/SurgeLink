using SurgeLink.Core.Monitoring;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>Parses the memory section of a Windows monitor sample.</summary>
public static class WindowsMemoryParser
{
    private const long BytesPerKilobyte = 1024;

    /// <summary>Parses <c>total</c>, <c>free</c>, <c>virtualTotal</c> and <c>virtualFree</c> kilobyte fields.</summary>
    /// <param name="memorySection">Rows shaped <c>key|kilobytes</c>.</param>
    /// <returns>The memory sample; missing fields are reported as zero.</returns>
    public static MemorySample Parse(string? memorySection)
    {
        var fields = WindowsSectionFields.Parse(memorySection);
        _ = WindowsSectionFields.TryGetLong(fields, "total", out var totalKilobytes);
        _ = WindowsSectionFields.TryGetLong(fields, "free", out var freeKilobytes);
        _ = WindowsSectionFields.TryGetLong(fields, "virtualTotal", out var virtualTotalKilobytes);
        _ = WindowsSectionFields.TryGetLong(fields, "virtualFree", out var virtualFreeKilobytes);

        var total = ToBytes(totalKilobytes);
        var available = Math.Min(ToBytes(freeKilobytes), total);
        var virtualTotal = ToBytes(virtualTotalKilobytes);
        var virtualFree = ToBytes(virtualFreeKilobytes);

        return new MemorySample
        {
            TotalBytes = total,
            AvailableBytes = available,
            CachedBytes = 0,
            SwapTotalBytes = virtualTotal,
            SwapUsedBytes = Math.Clamp(virtualTotal - virtualFree, 0, virtualTotal),
        };
    }

    private static long ToBytes(long kilobytes)
    {
        if (kilobytes <= 0)
        {
            return 0;
        }

        return kilobytes > long.MaxValue / BytesPerKilobyte ? long.MaxValue : kilobytes * BytesPerKilobyte;
    }
}