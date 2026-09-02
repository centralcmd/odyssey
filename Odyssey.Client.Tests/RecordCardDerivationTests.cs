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
        Insurers = [new PolicyContactReference { ContactId = Guid.NewGuid(), Name = "Insurer" }],
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

    // ── Tax statement settlement figure ─────────────────────────────────────────
    // The caption under the headline figure. A declared settlement is a fact; without one the figure
    // falls back to the reconciliation's estimate, and the estimate has to carry the same three
    // readings — an earlier version collapsed every non-null estimate into "outstanding (est.)", so a
    // year owed a refund read as a year owing tax.

    [Theory]
    [InlineData(150, "outstanding (est.)")]
    [InlineData(-150, "refund (est.)")]
    [InlineData(0, "settled (est.)")]
    public void TaxSettlement_AnEstimateReadsWithTheSignOfTheEstimate(int settle, string expected)
    {
        Assert.Equal(expected, TaxSettlementFigure.Word(declared: null, settle: settle));
    }

    [Fact]
    public void TaxSettlement_AYearWithNothingToGoOnSaysSo()
    {
        // No declared settlement AND no reconciliation to estimate from is not the same fact as an
        // estimate of zero: the first has no answer yet, the second says the year comes out even.
        Assert.Equal("awaiting assessment", TaxSettlementFigure.Word(declared: null, settle: null));
        Assert.Equal("settled (est.)", TaxSettlementFigure.Word(declared: null, settle: 0m));
    }

    [Theory]
    [InlineData(150, "additional tax to pay")]
    [InlineData(-150, "refund")]
    [InlineData(0, "settled")]
    public void TaxSettlement_ADeclaredSettlementDropsTheEstimateMarker(int declared, string expected)
    {
        // The declared figure wins outright — the estimate is not consulted, even when they disagree.
        Assert.Equal(expected, TaxSettlementFigure.Word(declared, settle: 9999m));
    }

    [Theory]
    [InlineData(150, OdsRecordFigureTone.Expense)]
    [InlineData(-150, OdsRecordFigureTone.Income)]
    [InlineData(0, OdsRecordFigureTone.Neutral)]
    public void TaxSettlement_ToneIsTheFinanceVocabulary(int settle, OdsRecordFigureTone expected)
    {
        Assert.Equal(expected, TaxSettlementFigure.Tone(settle));
    }

    [Fact]
    public void TaxSettlement_AnUnassessedYearIsToneless()
        => Assert.Equal(OdsRecordFigureTone.Neutral, TaxSettlementFigure.Tone(null));

    // ── Tax statement status ────────────────────────────────────────────────────
    // Archiving outranks the review status: an archived year is archived whatever it was flagged as.

    private static ExistingTaxStatement Statement(
        TaxStatementStatus status = TaxStatementStatus.New,
        DateTime? archived = null, string? comment = null, DateTime? statusChangedAt = null) => new()
    {
        TaxStatementId = Guid.NewGuid(),
        Name = "Tax Year 2026",
        FiscalYear = 2026,
        StartDate = new DateTime(2026, 1, 1),
        EndDate = new DateTime(2026, 12, 31),
        Status = status,
        StatusComment = comment,
        StatusChangedAt = statusChangedAt ?? default,
        Archived = archived,
    };

    [Theory]
    [InlineData(TaxStatementStatus.New, "New", "fiber_new", OdsInfoTileTone.Info)]
    [InlineData(TaxStatementStatus.Approved, "Approved", "check_circle", OdsInfoTileTone.Income)]
    [InlineData(TaxStatementStatus.Flagged, "Flagged", "flag", OdsInfoTileTone.Expense)]
    public void TaxStatementStatus_EachReviewStateHasItsOwnLabelGlyphAndTone(
        TaxStatementStatus status, string label, string icon, OdsInfoTileTone tone)
    {
        var s = Statement(status);

        Assert.Equal(label, TaxStatementStatusVisuals.Label(s));
        Assert.Equal(icon, TaxStatementStatusVisuals.Icon(s));
        Assert.Equal(tone, TaxStatementStatusVisuals.TileTone(s));
        Assert.True(TaxStatementStatusVisuals.Dot(s));
    }

    [Theory]
    [InlineData(TaxStatementStatus.New)]
    [InlineData(TaxStatementStatus.Approved)]
    [InlineData(TaxStatementStatus.Flagged)]
    public void TaxStatementStatus_ArchivedOutranksWhateverTheYearWasFlaggedAs(TaxStatementStatus status)
    {
        var s = Statement(status, archived: new DateTime(2026, 5, 4));

        Assert.Equal("Archived", TaxStatementStatusVisuals.Label(s));
        Assert.Equal("inventory_2", TaxStatementStatusVisuals.Icon(s));
        Assert.Equal(OdsInfoTileTone.Muted, TaxStatementStatusVisuals.TileTone(s));
        Assert.Equal(OdsChipTone.Outline, TaxStatementStatusVisuals.ChipTone(s));
        // An archived row is not a running state, so its chip drops the live dot.
        Assert.False(TaxStatementStatusVisuals.Dot(s));
    }

    [Fact]
    public void TaxStatementStatus_FootDatesTheStateItActuallyShows()
    {
        // Archived is what the tile reads, so the foot dates the archival — not the last review, which
        // is the date the tile is NOT showing.
        var s = Statement(TaxStatementStatus.Flagged,
            archived: new DateTime(2026, 5, 4), statusChangedAt: new DateTime(2026, 1, 9));

        Assert.Equal("May 04, 2026", TaxStatementStatusVisuals.Foot(s));
    }

    [Fact]
    public void TaxStatementStatus_FootPointsAtTheNoteWhenThereIsOne()
    {
        Assert.Equal("Jan 09, 2026 · see note above", TaxStatementStatusVisuals.Foot(
            Statement(TaxStatementStatus.Flagged, comment: "Needs review.", statusChangedAt: new DateTime(2026, 1, 9))));

        // Either part can stand alone.
        Assert.Equal("see note above", TaxStatementStatusVisuals.Foot(
            Statement(TaxStatementStatus.Flagged, comment: "Needs review.")));
        Assert.Equal("Jan 09, 2026", TaxStatementStatusVisuals.Foot(
            Statement(statusChangedAt: new DateTime(2026, 1, 9))));
    }

    [Fact]
    public void TaxStatementStatus_FootIsAbsentRatherThanEmpty()
        // A foot has to earn its place: with neither a date nor a note the tile renders no foot at all.
        => Assert.Null(TaxStatementStatusVisuals.Foot(Statement()));

    // ── Budget balances ─────────────────────────────────────────────────────────
    // The record card's two-branch rule, and where it deliberately parts company with the page
    // header's three-branch one.

    [Theory]
    [InlineData(-1, OdsRecordFigureTone.Expense)]
    [InlineData(1, OdsRecordFigureTone.Income)]
    public void BudgetBalance_APlanUnderWaterReadsAsAnExpense(int value, OdsRecordFigureTone expected)
    {
        Assert.Equal(expected, BudgetBalanceVisuals.FigureTone(value));
    }

    [Fact]
    public void BudgetBalance_ZeroReadsAsIncome_UnlikeTheHeaderScale()
    {
        // Deliberate, and the one place the two vocabularies differ. The card's figure is binary — a
        // plan is either under water or it is not — so a plan that spends exactly what it takes in
        // sits on the non-negative side. The page header's planned-balance colour is a three-branch
        // scale where zero is genuinely neither, and stays neutral. Pinned here so a future "tidy-up"
        // that unifies them has to be a decision rather than an accident.
        Assert.Equal(OdsRecordFigureTone.Income, BudgetBalanceVisuals.FigureTone(0m));
        Assert.Equal(OdsInfoTileTone.Income, BudgetBalanceVisuals.TileTone(0m));
    }

    [Theory]
    [InlineData(-1, OdsInfoTileTone.Expense)]
    [InlineData(1, OdsInfoTileTone.Income)]
    public void BudgetBalance_TheTilesFollowTheSameRuleAsTheFigure(int value, OdsInfoTileTone expected)
    {
        Assert.Equal(expected, BudgetBalanceVisuals.TileTone(value));
    }

    [Theory]
    [InlineData(0, "0 income lines")]
    [InlineData(1, "1 income line")]
    [InlineData(2, "2 income lines")]
    public void BudgetBalance_OnlyExactlyOneLineIsSingular(int count, string expected)
    {
        Assert.Equal(expected, BudgetBalanceVisuals.Lines(count, "income line"));
    }
}
