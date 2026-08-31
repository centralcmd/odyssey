using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Covers the pure derivations behind the record cards' headers — the pieces that decide what a
/// collapsed row actually says. They were extracted out of the cards precisely so they could be
/// asserted here: each was previously a private method whose only check was reading the rendered page.
/// </summary>
public class RecordCardDerivationTests
{
    private static readonly DateTime Today = new(2026, 8, 31);

    // ── Subscription status precedence ──────────────────────────────────────────
    // This replaced a version that rendered several chips at once (Ended AND Archived, say). The
    // lifecycle is ordered, so exactly one state may show; a regression back to stacking would
    // otherwise only be visible by eye.

    [Theory]
    [InlineData(false, false, false, "Active")]
    [InlineData(true, false, false, "Paused")]
    [InlineData(false, true, false, "Ended")]
    [InlineData(false, false, true, "Archived")]
    // Archived outranks everything: only an ended subscription can be archived.
    [InlineData(false, true, true, "Archived")]
    [InlineData(true, true, true, "Archived")]
    // Ended makes a pause moot.
    [InlineData(true, true, false, "Ended")]
    public void SubscriptionStatus_ResolvesExactlyOneState(bool paused, bool ended, bool archived, string expected)
    {
        var state = OdsSubscriptionStatus.Resolve(paused, ended, archived, showActive: true);

        Assert.Equal(expected, state!.Label);
    }

    [Fact]
    public void SubscriptionStatus_ShowsNothingForAPlainActiveRow_UnlessAsked()
    {
        // A list of untouched subscriptions should not carry a chip on every row.
        Assert.Null(OdsSubscriptionStatus.Resolve(false, false, false, showActive: false));
        Assert.Equal("Active", OdsSubscriptionStatus.Resolve(false, false, false, showActive: true)!.Label);

        // A real state still shows even when the Active chip is suppressed.
        Assert.Equal("Paused", OdsSubscriptionStatus.Resolve(true, false, false, showActive: false)!.Label);
    }

    // ── Contract "has ended" ────────────────────────────────────────────────────
    // The client disables Archive on the same predicate the service refuses it on. Both call this one
    // implementation, so these assertions pin the boundary for the API as well as the menu.

    [Fact]
    public void ContractLifecycle_TermIsOverOnlyAfterItsEndDateHasPassed()
    {
        Assert.False(ContractLifecycle.HasEnded(Today.AddDays(1), null, Today));
        // Today is not "ended": DeriveStatus calls a term Expired only once end < today.
        Assert.False(ContractLifecycle.HasEnded(Today, null, Today));
        Assert.True(ContractLifecycle.HasEnded(Today.AddDays(-1), null, Today));
    }

    [Fact]
    public void ContractLifecycle_ASettledOneOffIsOverOnTheDayItCompletes()
    {
        // The asymmetry is deliberate and matches DeriveStatus: a completion date settles ON the day,
        // where a term has to have lapsed. A settled one-off still derives as Active, which is why the
        // gate tests dates rather than comparing against ContractStatus.Expired.
        Assert.False(ContractLifecycle.HasEnded(null, Today.AddDays(1), Today));
        Assert.True(ContractLifecycle.HasEnded(null, Today, Today));
        Assert.True(ContractLifecycle.HasEnded(null, Today.AddDays(-1), Today));
    }

    [Fact]
    public void ContractLifecycle_AContractWithNeitherDateHasNotEnded()
    {
        Assert.False(ContractLifecycle.HasEnded(null, null, Today));
    }

    // ── Insurance headline ──────────────────────────────────────────────────────
    // Every state that HAS a period headlines on a date; NoCoverage is the one state with none,
    // because it is exactly the case where no period was ever recorded.

    private static InsurancePolicyListItem Policy(
        CoverageStatus status, DateTime? currentEnd = null, DateTime? latestEnd = null, DateTime? earliestStart = null) => new()
    {
        InsurancePolicyId = Guid.NewGuid(),
        Name = "Policy",
        Insurer = new InsurerReference { ContactId = Guid.NewGuid(), Name = "Insurer" },
        CoverageStatus = status,
        CurrentRenewalEndDate = currentEnd,
        LatestRenewalEndDate = latestEnd,
        EarliestRenewalStartDate = earliestStart,
    };

    [Fact]
    public void InsuranceHeadline_ExpiredShowsWhenCoverRanOut_AndHowLongAgo()
    {
        var h = InsuranceHeadline.Compute(
            Policy(CoverageStatus.Lapsed, latestEnd: new DateTime(2025, 12, 31)), Today);

        Assert.Equal("Dec 31, 2025", h.Value);
        Assert.Equal("expired 243 days ago", h.Word);
        Assert.Equal("lapsed", h.Cls);
    }

    [Fact]
    public void InsuranceHeadline_NeverCoveredSaysSo_RatherThanShowingADate()
    {
        // The distinction that matters: cover that ran out is not the same fact as cover that never
        // existed, and only the second has no date to show.
        var h = InsuranceHeadline.Compute(Policy(CoverageStatus.NoCoverage), Today);

        Assert.Equal("No coverage", h.Value);
        Assert.Equal("no coverage yet", h.Word);
    }

    [Fact]
    public void InsuranceHeadline_UpcomingShowsWhenCoverBegins()
    {
        var h = InsuranceHeadline.Compute(
            Policy(CoverageStatus.Upcoming, earliestStart: new DateTime(2026, 10, 1)), Today);

        Assert.Equal("Oct 01, 2026", h.Value);
        Assert.Equal("starts in 31 days", h.Word);
    }

    [Theory]
    [InlineData(CoverageStatus.Active, "expires in 91 days", "")]
    [InlineData(CoverageStatus.ExpiringSoon, "expires in 91 days", "soon")]
    public void InsuranceHeadline_LiveCoverShowsItsOwnPeriodEnd(CoverageStatus status, string word, string cls)
    {
        var h = InsuranceHeadline.Compute(Policy(status, currentEnd: new DateTime(2026, 11, 30)), Today);

        Assert.Equal("Nov 30, 2026", h.Value);
        Assert.Equal(word, h.Word);
        Assert.Equal(cls, h.Cls);
    }

    [Fact]
    public void InsuranceHeadline_SingularDayAndTodayReadCorrectly()
    {
        Assert.Equal("expires today",
            InsuranceHeadline.Compute(Policy(CoverageStatus.Active, currentEnd: Today), Today).Word);
        Assert.Equal("expires in 1 day",
            InsuranceHeadline.Compute(Policy(CoverageStatus.Active, currentEnd: Today.AddDays(1)), Today).Word);
        Assert.Equal("expired today",
            InsuranceHeadline.Compute(Policy(CoverageStatus.Lapsed, latestEnd: Today), Today).Word);
    }

    [Fact]
    public void InsuranceHeadline_ArchivedShowsItsLastPeriod_OrTheWordWhenItNeverHadOne()
    {
        Assert.Equal("Oct 31, 2025",
            InsuranceHeadline.Compute(Policy(CoverageStatus.Archived, latestEnd: new DateTime(2025, 10, 31)), Today).Value);
        Assert.Equal("Archived",
            InsuranceHeadline.Compute(Policy(CoverageStatus.Archived), Today).Value);
    }
}
