using Odyssey.Core.Finance;
using Odyssey.Context;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The shared temporal "value in force" rule (used by the term, estimate, and totals resolvers): the
/// greatest <c>EffectiveFrom</c> wins, ties broken by the most recently created row
/// (<c>CreatedAtUtc</c>). These pin the consolidated single-home implementation so a future change to
/// the tie-break is caught here rather than across four resolvers.
/// </summary>
public class EffectiveDatedExtensionsTests
{
    private static AccountEstimate Estimate(DateTime effectiveFrom, DateTime createdAtUtc, decimal value) =>
        new()
        {
            AccountEstimateId = Guid.NewGuid(),
            EffectiveFrom = effectiveFrom,
            CreatedAtUtc = createdAtUtc,
            Value = value,
        };

    [Fact]
    public void MostEffective_PicksGreatestEffectiveFrom()
    {
        var older = Estimate(new DateTime(2024, 1, 1), new DateTime(2024, 6, 1), 100m);
        var newer = Estimate(new DateTime(2024, 3, 1), new DateTime(2024, 1, 1), 200m);

        // Newer EffectiveFrom wins even though it was created earlier.
        Assert.Equal(200m, new[] { older, newer }.MostEffective()!.Value);
    }

    [Fact]
    public void MostEffective_OnEqualEffectiveFrom_BreaksTieByLatestCreatedAt()
    {
        var sameDate = new DateTime(2024, 5, 1);
        var createdFirst = Estimate(sameDate, new DateTime(2024, 5, 1, 8, 0, 0), 100m);
        var createdLater = Estimate(sameDate, new DateTime(2024, 5, 1, 9, 0, 0), 250m);

        // Same EffectiveFrom → the more recently created row is in force, regardless of input order.
        Assert.Equal(250m, new[] { createdLater, createdFirst }.MostEffective()!.Value);
        Assert.Equal(250m, new[] { createdFirst, createdLater }.MostEffective()!.Value);
    }

    [Fact]
    public void MostEffective_OnEmptySequence_ReturnsNull()
    {
        Assert.Null(Array.Empty<AccountEstimate>().MostEffective());
    }
}
