namespace SurgeLink.Protocols.Ssh.Monitoring;

/// <summary>Raw jiffy counters of one CPU line of <c>/proc/stat</c>.</summary>
/// <param name="User">Jiffies spent in user mode.</param>
/// <param name="Nice">Jiffies spent in user mode with low priority.</param>
/// <param name="System">Jiffies spent in system mode.</param>
/// <param name="Idle">Jiffies spent idle.</param>
/// <param name="Iowait">Jiffies waiting for I/O to complete; zero on older kernels.</param>
/// <param name="Irq">Jiffies servicing interrupts.</param>
/// <param name="SoftIrq">Jiffies servicing soft interrupts.</param>
/// <param name="Steal">Jiffies stolen by a hypervisor.</param>
/// <param name="Guest">Jiffies running a guest; already counted in <paramref name="User"/>.</param>
/// <param name="GuestNice">Jiffies running a low priority guest; already counted in <paramref name="Nice"/>.</param>
public readonly record struct ProcCpuTimes(
    long User,
    long Nice,
    long System,
    long Idle,
    long Iowait = 0,
    long Irq = 0,
    long SoftIrq = 0,
    long Steal = 0,
    long Guest = 0,
    long GuestNice = 0)
{
    /// <summary>Sum of all counters, used as the utilisation denominator.</summary>
    public long Total => User + Nice + System + Idle + Iowait + Irq + SoftIrq + Steal;

    /// <summary>Idle jiffies, including the time spent waiting for I/O.</summary>
    public long IdleTotal => Idle + Iowait;

    /// <summary>Subtracts <paramref name="previous"/> from <paramref name="current"/> field by field.</summary>
    /// <param name="previous">Earlier sample.</param>
    /// <param name="current">Later sample.</param>
    public static ProcCpuTimes Delta(ProcCpuTimes previous, ProcCpuTimes current) => new(
        current.User - previous.User,
        current.Nice - previous.Nice,
        current.System - previous.System,
        current.Idle - previous.Idle,
        current.Iowait - previous.Iowait,
        current.Irq - previous.Irq,
        current.SoftIrq - previous.SoftIrq,
        current.Steal - previous.Steal,
        current.Guest - previous.Guest,
        current.GuestNice - previous.GuestNice);
}
