using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// A test that bounds the process-wide live heap (<see cref="GC.GetTotalMemory(bool)"/>) must not run beside anything
/// else, or another class's allocations count against its bound.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class MemoryProbeCollection
{
    public const string Name = "memory-probe";
}
