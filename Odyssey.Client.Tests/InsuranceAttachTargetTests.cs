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
        Insurers = [new PolicyContactReference { ContactId = Guid.NewGuid(), Name = "Insurer" }],
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

    /// <summary>
    /// The caller's own period wins over the inference. The period panel passes its id, and that is
    /// the user's choice — an inference that overrode it would file the document on a period they
    /// were not looking at.
    /// </summary>
    [Fact]
    public void An_explicit_period_is_used_as_given()
    {
        var current = Period(new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc));
        var chosen = Period(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            chosen.PolicyRenewalId,
            InsuranceAttachTarget.Resolve(Policy(current, current, chosen), chosen.PolicyRenewalId));
    }

    [Fact]
    public void With_no_explicit_period_the_inference_is_used()
    {
        var latest = Period(new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var older = Period(new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(latest.PolicyRenewalId, InsuranceAttachTarget.Resolve(Policy(null, older, latest), null));
    }

    /// <summary>
    /// The race the card guards: the row menu's gate said there was a period, and by the time the
    /// dialog opened there was not. Resolving to Empty is what lets the caller refuse rather than
    /// post a default Guid to a route that would 404.
    /// </summary>
    [Fact]
    public void With_neither_an_explicit_period_nor_one_to_infer_it_resolves_to_empty()
    {
        Assert.Equal(Guid.Empty, InsuranceAttachTarget.Resolve(Policy(null), null));
    }
}
