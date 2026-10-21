using System.Text;

namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>
/// Builds the PowerShell commands used to sample a Windows guest.
/// </summary>
/// <remarks>
/// The guest needs <c>powershell.exe</c> (Windows PowerShell 5.1 or PowerShell 7) on the
/// PATH of the SSH user. Scripts are written with single-quoted PowerShell literals and
/// contain no double-quoted expansions; they are transferred with <c>-EncodedCommand</c>
/// (UTF-16LE base64) so they survive both cmd.exe and PowerShell login shells without
/// any quoting, escaping or <c>$</c> expansion on the way in.
/// </remarks>
public static class WindowsMonitorScript
{
    private const string LimitPlaceholder = "__LIMIT__";
    private const string PowerShellPrefix =
        "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ";

    /// <summary>Section name of the per-core CPU counters.</summary>
    public const string CpuSection = "cpu";

    /// <summary>Section name of the <c>Win32_Processor</c> fallback counters.</summary>
    public const string ProcessorSection = "processor";

    /// <summary>Section name of the memory counters.</summary>
    public const string MemorySection = "memory";

    /// <summary>Section name of the process, thread and uptime counters.</summary>
    public const string SystemSection = "system";

    /// <summary>Section name of the logical disk usage rows.</summary>
    public const string DiskSection = "disks";

    /// <summary>Section name of the logical disk read/write throughput rows.</summary>
    public const string DiskIoSection = "diskio";

    /// <summary>Section name of the network adapter statistics rows.</summary>
    public const string NetworkSection = "network";

    /// <summary>Section name of the raw CIM network counter fallback rows.</summary>
    public const string NetworkFallbackSection = "networkfallback";

    /// <summary>Trailing marker emitted after the last section.</summary>
    public const string EndSection = "end";

    /// <summary>The complete PowerShell script executed for one snapshot.</summary>
    public static string Script { get; } = """
        try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
        $ErrorActionPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        function Emit([string]$name) { Write-Output ('@@@SURGELINK:' + $name) }
        function Field([string]$key, $value) { Write-Output ($key + '|' + [string]$value) }

        Emit 'cpu'
        Get-CimInstance -ClassName Win32_PerfFormattedData_PerfOS_Processor | ForEach-Object { Write-Output ([string]$_.Name + '|' + [string]$_.PercentProcessorTime) }

        Emit 'processor'
        Get-CimInstance -ClassName Win32_Processor | ForEach-Object { Write-Output ([string]$_.LoadPercentage + '|' + [string]$_.NumberOfLogicalProcessors + '|' + [string]$_.NumberOfCores) }

        Emit 'memory'
        $operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem
        Field 'total' $operatingSystem.TotalVisibleMemorySize
        Field 'free' $operatingSystem.FreePhysicalMemory
        Field 'virtualTotal' $operatingSystem.TotalVirtualMemorySize
        Field 'virtualFree' $operatingSystem.FreeVirtualMemory

        Emit 'system'
        Field 'processes' $operatingSystem.NumberOfProcesses
        Field 'boot' $operatingSystem.LastBootUpTime.ToUniversalTime().ToString('o')
        $system = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfOS_System
        Field 'threads' $system.Threads
        Field 'queue' $system.ProcessorQueueLength

        Emit 'disks'
        Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object { Write-Output ([string]$_.DeviceID + '|' + [string]$_.Size + '|' + [string]$_.FreeSpace + '|' + [string]$_.FileSystem) }

        Emit 'diskio'
        Get-CimInstance -ClassName Win32_PerfFormattedData_PerfDisk_LogicalDisk | Where-Object { $_.Name -ne '_Total' } | ForEach-Object { Write-Output ([string]$_.Name + '|' + [string]$_.DiskReadBytesPersec + '|' + [string]$_.DiskWriteBytesPersec) }

        Emit 'network'
        Get-NetAdapterStatistics | ForEach-Object { Write-Output ([string]$_.Name + '|' + [string]$_.ReceivedBytes + '|' + [string]$_.SentBytes + '|' + [string]$_.ReceivedUnicastPackets + '|' + [string]$_.SentUnicastPackets) }

        Emit 'networkfallback'
        Get-CimInstance -ClassName Win32_PerfRawData_Tcpip_NetworkInterface | ForEach-Object { Write-Output ([string]$_.Name + '|' + [string]$_.BytesReceivedPersec + '|' + [string]$_.BytesSentPersec + '|' + [string]$_.PacketsReceivedPersec + '|' + [string]$_.PacketsSentPersec) }

        Emit 'end'
        """;

    /// <summary>Full command line sent over SSH to collect one snapshot.</summary>
    public static string SampleCommand { get; } = BuildCommand(Script);

    /// <summary>Builds a command line that runs <paramref name="script"/> through <c>-EncodedCommand</c>.</summary>
    /// <param name="script">PowerShell script text.</param>
    public static string BuildCommand(string script)
    {
        ArgumentException.ThrowIfNullOrEmpty(script);
        return PowerShellPrefix + Encode(script);
    }

    /// <summary>Encodes a script the way <c>-EncodedCommand</c> expects it (UTF-16LE base64).</summary>
    /// <param name="script">PowerShell script text.</param>
    public static string Encode(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    /// <summary>Builds the two top process commands for a snapshot: formatted perf data first, <c>Get-Process</c> fallback second.</summary>
    /// <param name="maxCount">Maximum number of rows requested; clamped to the process-list bounds.</param>
    public static IReadOnlyList<string> BuildTopProcessCommands(int maxCount)
    {
        var limit = Math.Clamp(maxCount, 1, PsParser.MaximumProcessCount);
        return new[]
        {
            BuildCommand(BuildPerfProcessScript(limit)),
            BuildCommand(BuildGetProcessScript(limit)),
        };
    }

    /// <summary>Builds the <c>Win32_PerfFormattedData_PerfProc_Process</c> top process script.</summary>
    /// <param name="limit">Maximum number of rows.</param>
    public static string BuildPerfProcessScript(int limit)
    {
        var script = """
            try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
            $ErrorActionPreference = 'SilentlyContinue'
            $ProgressPreference = 'SilentlyContinue'
            Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process |
                Where-Object { $_.Name -ne '_Total' -and $_.Name -ne 'Idle' } |
                Sort-Object PercentProcessorTime -Descending |
                Select-Object -First __LIMIT__ |
                ForEach-Object { Write-Output ([string]$_.IDProcess + '|' + [string]$_.Name + '|' + [string]$_.PercentProcessorTime + '|' + [string]$_.WorkingSet + '|' + [string]$_.ThreadCount) }
            """;

        return script.Replace(LimitPlaceholder, Math.Clamp(limit, 1, PsParser.MaximumProcessCount).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>Builds the <c>Get-Process</c> fallback top process script; CPU seconds are normalised by process age.</summary>
    /// <param name="limit">Maximum number of rows.</param>
    public static string BuildGetProcessScript(int limit)
    {
        var script = """
            try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
            $ErrorActionPreference = 'SilentlyContinue'
            $ProgressPreference = 'SilentlyContinue'
            $now = Get-Date
            Get-Process |
                Sort-Object CPU -Descending |
                Select-Object -First __LIMIT__ |
                ForEach-Object {
                    $age = 0.0
                    try { $age = ($now - $_.StartTime).TotalSeconds } catch { $age = 0.0 }
                    $percent = 0.0
                    if ($age -gt 0 -and $null -ne $_.CPU) { $percent = [math]::Round(($_.CPU / $age) * 100.0, 1) }
                    $threads = 0
                    try { $threads = @($_.Threads).Count } catch { $threads = 0 }
                    Write-Output ([string]$_.Id + '|' + [string]$_.ProcessName + '|' + [string]$percent + '|' + [string]$_.WorkingSet64 + '|' + [string]$threads)
                }
            """;

        return script.Replace(LimitPlaceholder, Math.Clamp(limit, 1, PsParser.MaximumProcessCount).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}