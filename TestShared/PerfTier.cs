// Linked (not referenced) into Odyssey.Core.Tests and Odyssey.IntegrationTests beside TestTierGate.cs, which
// holds the rule; this is the xUnit half, so TestTierGate itself stays free of a test-framework dependency.

using Xunit;

namespace Odyssey.Testing;

/// <summary>
/// The opt-in perf tier (<see cref="TestTierGate.Perf"/>): volume, memory and time-budget tests that skip in the
/// per-PR run and run on the release-please Release PR (`perf.yml`). A perf test is a <c>[SkippableFact]</c> carrying
/// <c>[Trait(PerfTier.TraitName, PerfTier.TraitValue)]</c> — the filter `perf.yml` selects them by — whose first
/// statement is <see cref="SkipUnlessRequested"/>.
/// </summary>
internal static class PerfTier
{
    public const string TraitName = "Category";

    public const string TraitValue = "Perf";

    /// <summary>Skips the calling test unless <c>ODYSSEY_REQUIRE_TIER</c> names <c>perf</c>.</summary>
    public static void SkipUnlessRequested()
    {
        var reason = TestTierGate.PerfSkipReason();
        Skip.If(reason is not null, reason);
    }

    /// <summary>
    /// <see cref="SkipUnlessRequested"/>, then a prerequisite the test also needs: absent while the tier is required
    /// is a failure, never a skip.
    /// </summary>
    public static void SkipUnlessRequested(bool prerequisiteAvailable, string? prerequisiteReason)
    {
        SkipUnlessRequested();
        if (!prerequisiteAvailable)
        {
            throw TestTierGate.PerfPrerequisiteMissing(prerequisiteReason);
        }
    }
}
