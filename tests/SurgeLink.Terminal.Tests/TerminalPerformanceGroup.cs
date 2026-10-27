using Xunit;

namespace SurgeLink.Terminal.Tests;

/// <summary>
/// Serializes the wall clock performance guards against the rest of the suite, so a
/// throughput measurement never races dozens of other test classes for the CPU.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TerminalPerformanceGroup
{
    /// <summary>Collection name shared with <see cref="PerformanceTests"/>.</summary>
    public const string Name = "Terminal performance";
}
