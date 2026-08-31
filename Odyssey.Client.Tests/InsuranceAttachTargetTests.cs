using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Which period a row-menu attach targets (issue #26 §3). The row menu, unlike the period panel, has no
/// period in hand, so the target is inferred — and the dialog names it in its body precisely because
/// the user did not choose it.
/// </summary>
public class InsuranceAttachTargetTests
{
    private static readonly DateTime Created = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ExistingPolicyRenewal Period(DateTime to, DateTime? createdAt = null) => new()
    {
        PolicyRenewalId = Guid.NewGuid(),
        InsurancePolicyId = Guid.NewGuid(),
        FromDate = to.AddYears(-1),
        ToDate = to,
        Premium = 1m,
        PremiumCurrencyCode = "USD",
        CoverageAmount = 1m,
        CoverageCurrencyCode = "USD",
        CreatedAtUtc = createdAt ?? Created,
    };

    private static ExistingInsurancePolicy Policy(
        ExistingPolicyRenewal? current, params ExistingPolicyRenewal[] renewals) => new()
    {
        InsurancePolicyId = Guid.NewGuid(),
        Name = "Cover",
        Insurer = new InsurerReference { ContactId = Guid.NewGuid(), Name = "Insurer" },
        CurrentRenewal = current,
        Renewals = [.. renewals],
        CreatedAtUtc = Created,
    };

    [Fact]
    public void The_current_period_wins_when_there_is_one()
    {
        var current = Period(new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc));
        var later = Period(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // Deliberately alongside a period that ends LATER, so "latest ToDate" alone would pick wrong.
        Assert.Equal(current.PolicyRenewalId, InsuranceAttachTarget.For(Policy(current, later, current)));
    }

    /// <summary>
    /// The fallback, reached by every lapsed and every upcoming policy — including the ones this
    /// change's own migration creates, whose placeholder period is dated in the past.
    /// </summary>
    [Fact]
    public void With_no_current_period_the_latest_ending_one_wins()
    {
        var oldest = Period(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newest = Period(new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var middle = Period(new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(newest.PolicyRenewalId, InsuranceAttachTarget.For(Policy(null, oldest, newest, middle)));
    }

    [Fact]
    public void Periods_ending_on_the_same_day_are_broken_by_the_later_creation()
    {
        var same = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var first = Period(same, createdAt: new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var second = Period(same, createdAt: new DateTime(2022, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(second.PolicyRenewalId, InsuranceAttachTarget.For(Policy(null, first, second)));
    }

    /// <summary>
    /// Empty, not an exception and not a stray id: the row action is gated on RenewalCount, and the
    /// caller treats Guid.Empty as "there is nowhere to attach this".
    /// </summary>
    [Fact]
    public void A_policy_with_no_period_resolves_to_nothing()
    {
        Assert.Equal(Guid.Empty, InsuranceAttachTarget.For(Policy(null)));
    }
}
