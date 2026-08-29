using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextBillingInterval = Odyssey.Context.BillingInterval;
using DtoBillingInterval = Odyssey.Dtos.Finance.BillingInterval;
using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

/// <summary>
/// Unit coverage for <see cref="SubscriptionService.GetSummary"/> (issue #293 summary follow-up): the
/// cadence-normalized multi-currency run-rate (converted into a base currency, excluding currencies
/// with no rate), the top cost driver, the derived upcoming-renewals list (next-billing computed from
/// the anchor + interval, archived/paused skipped, look-ahead window enforced), and the status/interval
/// counts. "Today" is pinned to <see cref="FixedNow"/>.
/// </summary>
public class SubscriptionSummaryTests
{
    private static readonly DateTime FixedNow = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private static SubscriptionService CreateService(
        OdysseyContext context, ISystemSettingsLookup? settings = null) =>
        new(context, TestContextFactory.EmptyContactLookup(), settings ?? new FakeSystemSettingsLookup(),
            new FixedTimeProvider(FixedNow), new CurrencyConversionService(context));

    private static async Task SeedRate(OdysseyContext context, string from, string to, decimal rate)
    {
        await new ExchangeRateService(context).Create(new NewExchangeRate
        {
            FromCurrencyCode = from,
            ToCurrencyCode = to,
            Rate = rate,
            AsOf = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
    }

    private static Subscription Sub(
        decimal amount, string currency, ContextBillingInterval interval, DateOnly firstBilling,
        int intervalCount = 1, DateOnly? endDate = null, DateTime? paused = null, DateTime? archived = null,
        string name = "Sub", DateTime? createdAt = null) => new()
    {
        SubscriptionId = Guid.NewGuid(),
        Name = name,
        Amount = amount,
        CurrencyCode = currency,
        Interval = interval,
        IntervalCount = intervalCount,
        StartDate = new DateOnly(2026, 1, 1),
        EndDate = endDate,
        FirstBillingDate = firstBilling,
        Paused = paused,
        Archived = archived,
        CreatedAtUtc = createdAt ?? FixedNow,
    };

    [Fact]
    public async Task RunRate_NormalizesCadence_SingleCurrency()
    {
        await using var context = TestContextFactory.Create();
        context.Subscriptions.AddRange(
            Sub(10m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), name: "Monthly"),
            Sub(120m, "USD", ContextBillingInterval.Yearly, new DateOnly(2026, 1, 10), name: "Yearly"));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        // Monthly-equivalent: 10 (monthly) + 120/12 (yearly) = 20; yearly: 120 + 120 = 240.
        Assert.Equal(20m, summary.RunRate.ConvertedMonthly);
        Assert.Equal(240m, summary.RunRate.ConvertedYearly);
        Assert.Single(summary.RunRate.Rows);
        Assert.Empty(summary.RunRate.ExcludedCurrencies);
        Assert.Equal("USD", summary.RunRate.BaseCurrency);
    }

    [Fact]
    public async Task RunRate_IntervalCount_DividesMonthlyEquivalent()
    {
        await using var context = TestContextFactory.Create();
        // €30 billed every 3 months → monthly-equivalent €10, yearly €120.
        context.Subscriptions.Add(Sub(30m, "EUR", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), intervalCount: 3));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("EUR");

        Assert.Equal(10m, summary.RunRate.ConvertedMonthly);
        Assert.Equal(120m, summary.RunRate.ConvertedYearly);
    }

    [Fact]
    public async Task RunRate_MultiCurrency_ConvertsWhereRateExists_ExcludesOthers()
    {
        await using var context = TestContextFactory.Create();
        await SeedRate(context, "EUR", "USD", 1.1m); // no SEK->USD rate seeded
        context.Subscriptions.AddRange(
            Sub(10m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), name: "US"),
            Sub(20m, "EUR", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), name: "EU"),
            Sub(30m, "SEK", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), name: "SE"));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        // 10 (USD, 1:1) + 20*1.1 (EUR) = 32; SEK has no rate → excluded from the total.
        Assert.Equal(32m, summary.RunRate.ConvertedMonthly);
        Assert.Equal(3, summary.RunRate.Rows.Count);
        Assert.Contains("SEK", summary.RunRate.ExcludedCurrencies);
        Assert.DoesNotContain("EUR", summary.RunRate.ExcludedCurrencies);
    }

    [Fact]
    public async Task RunRate_TopDriver_IsBiggestMonthlyEquivalentInBase()
    {
        await using var context = TestContextFactory.Create();
        await SeedRate(context, "EUR", "USD", 1.2m);
        context.Subscriptions.AddRange(
            Sub(15m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), name: "Cheaper"),
            Sub(13m, "EUR", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), name: "Pricier")); // 13*1.2 = 15.6
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        Assert.Equal("Pricier", summary.RunRate.TopDriver?.Name);
    }

    [Fact]
    public async Task RunRate_And_Renewals_SkipPausedArchivedAndEnded()
    {
        await using var context = TestContextFactory.Create();
        context.Subscriptions.AddRange(
            Sub(10m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), name: "Live"),
            Sub(10m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), paused: FixedNow, name: "Paused"),
            Sub(10m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), archived: FixedNow, name: "Archived"),
            Sub(10m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), endDate: new DateOnly(2026, 3, 1), name: "Ended"));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        // Only "Live" contributes to the run-rate (paused/archived/ended all excluded).
        Assert.Equal(10m, summary.RunRate.ConvertedMonthly);
        Assert.Single(summary.RunRate.Rows);
        Assert.All(summary.UpcomingRenewals, r => Assert.Equal("Live", r.Name));
    }

    [Fact]
    public async Task UpcomingRenewals_DerivesNextBilling_WithinWindow()
    {
        await using var context = TestContextFactory.Create();
        context.Subscriptions.AddRange(
            // Monthly, anchored on the 10th → next charge 2026-07-10 (25 days out) → inside the 45-day window.
            Sub(10m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), name: "Soon"),
            // Yearly, anchored 2026-01-01 → next 2027-01-01 → far outside the window.
            Sub(99m, "USD", ContextBillingInterval.Yearly, new DateOnly(2026, 1, 1), name: "FarOff"));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        var renewal = Assert.Single(summary.UpcomingRenewals);
        Assert.Equal("Soon", renewal.Name);
        Assert.Equal(new DateOnly(2026, 7, 10), renewal.NextBillingDate);
        Assert.Equal(25, renewal.DaysUntil);
    }

    [Fact]
    public async Task Counts_ByStatus_And_Interval()
    {
        await using var context = TestContextFactory.Create();
        context.Subscriptions.AddRange(
            Sub(10m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), name: "Active"),
            Sub(10m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), paused: FixedNow, name: "Paused"),
            Sub(10m, "USD", ContextBillingInterval.Yearly, new DateOnly(2026, 1, 10), archived: FixedNow, name: "Archived"));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        Assert.Equal(2, summary.Total); // live = active + paused
        Assert.Equal(1, summary.CountsByStatus.Active);
        Assert.Equal(1, summary.CountsByStatus.Paused);
        Assert.Equal(1, summary.CountsByStatus.Archived);
        // Interval breakdown covers the live set only → Monthly ×2, no Yearly (it is archived).
        var monthly = Assert.Single(summary.CountsByInterval);
        Assert.Equal(DtoBillingInterval.Monthly, monthly.Interval);
        Assert.Equal(2, monthly.Count);
    }

    [Fact]
    public async Task Counts_Ended_IsDerived_SupersedesPaused_AndLeavesRunRate()
    {
        await using var context = TestContextFactory.Create();
        context.Subscriptions.AddRange(
            // Live billing.
            Sub(10m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), name: "Active"),
            // EndDate in the past → Ended (not archived).
            Sub(10m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10),
                endDate: new DateOnly(2026, 5, 1), name: "Lapsed"),
            // Paused AND ended → Ended supersedes Paused.
            Sub(10m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10),
                endDate: new DateOnly(2026, 5, 1), paused: FixedNow, name: "PausedButEnded"));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        Assert.Equal(1, summary.CountsByStatus.Active);
        Assert.Equal(0, summary.CountsByStatus.Paused);
        Assert.Equal(2, summary.CountsByStatus.Ended);
        Assert.Equal(0, summary.CountsByStatus.Archived);
        // Total counts every non-archived subscription (active + paused + ended).
        Assert.Equal(3, summary.Total);
        // Ended subscriptions no longer bill → only the single Active one drives the run-rate.
        Assert.Equal(10m, summary.RunRate.ConvertedMonthly);
        Assert.Single(summary.RunRate.Rows);
        Assert.Equal(1, summary.RunRate.Rows[0].Count);
    }

    [Fact]
    public async Task Counts_Ended_IncludesExactlyToday_AndDropsItFromRunRate()
    {
        // Boundary: a term ending exactly today (FixedNow = 2026-06-15) is Ended (EndDate ≤ today) and
        // no longer bills — this pins the deliberate run-rate change from `>= today` to `> today`.
        await using var context = TestContextFactory.Create();
        context.Subscriptions.AddRange(
            Sub(10m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10),
                endDate: new DateOnly(2026, 6, 15), name: "EndsToday"),
            // Ends tomorrow → still Active and still billing.
            Sub(20m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10),
                endDate: new DateOnly(2026, 6, 16), name: "EndsTomorrow"));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        Assert.Equal(1, summary.CountsByStatus.Active);
        Assert.Equal(1, summary.CountsByStatus.Ended);
        // Only the still-live "EndsTomorrow" contributes to the run-rate.
        Assert.Equal(20m, summary.RunRate.ConvertedMonthly);
        Assert.Single(summary.RunRate.Rows);
    }

    [Fact]
    public async Task Counts_Archived_SupersedesEnded()
    {
        // Top of the precedence chain (Archived > Ended > Paused > Active): an archived subscription
        // with a lapsed EndDate is counted Archived, never Ended.
        await using var context = TestContextFactory.Create();
        context.Subscriptions.Add(
            Sub(10m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10),
                endDate: new DateOnly(2026, 5, 1), archived: FixedNow, name: "ArchivedAndLapsed"));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        Assert.Equal(1, summary.CountsByStatus.Archived);
        Assert.Equal(0, summary.CountsByStatus.Ended);
        Assert.Equal(0, summary.CountsByStatus.Active);
        Assert.Equal(0, summary.Total); // archived is not "live"
    }

    [Fact]
    public async Task GetSummary_BlankBaseCurrency_UsesMostCommonBillingCurrency()
    {
        await using var context = TestContextFactory.Create();
        context.Subscriptions.AddRange(
            Sub(10m, "EUR", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), name: "E1"),
            Sub(10m, "EUR", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), name: "E2"),
            Sub(10m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), name: "U1"));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary(baseCurrency: null);

        Assert.Equal("EUR", summary.RunRate.BaseCurrency);
    }

    [Fact]
    public async Task GetSummary_NoSubscriptions_FallsBackToUsd_WithNullTotals()
    {
        await using var context = TestContextFactory.Create();

        var summary = await CreateService(context).GetSummary(baseCurrency: null);

        Assert.Equal(0, summary.Total);
        Assert.Equal("USD", summary.RunRate.BaseCurrency);
        Assert.Null(summary.RunRate.ConvertedMonthly);
        Assert.Null(summary.RunRate.ConvertedYearly);
        Assert.Empty(summary.UpcomingRenewals);
        Assert.Null(summary.RunRate.TopDriver);
    }

    [Fact]
    public async Task GetSummary_BaseCurrency_IsCaseInsensitive()
    {
        await using var context = TestContextFactory.Create();
        context.Subscriptions.Add(Sub(10m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10)));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("usd");

        Assert.Equal("USD", summary.RunRate.BaseCurrency);
        Assert.Equal(10m, summary.RunRate.ConvertedMonthly); // USD sub converts 1:1 against normalized base
    }

    // ── Daily / Weekly cadence (previously uncovered) ──

    [Fact]
    public async Task RunRate_Daily_NormalizesCadence()
    {
        await using var context = TestContextFactory.Create();
        context.Subscriptions.Add(Sub(12m, "USD", ContextBillingInterval.Daily, new DateOnly(2026, 1, 10)));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        // Daily factor: monthly = amount × 365.25/12 = 365.25; yearly = amount × 365.25 = 4383.00.
        Assert.Equal(365.25m, summary.RunRate.ConvertedMonthly);
        Assert.Equal(4383m, summary.RunRate.ConvertedYearly);
    }

    [Fact]
    public async Task RunRate_Weekly_NormalizesCadence_AndRoundsToTwoPlaces()
    {
        await using var context = TestContextFactory.Create();
        context.Subscriptions.Add(Sub(10m, "USD", ContextBillingInterval.Weekly, new DateOnly(2026, 1, 10)));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        // Weekly factor: monthly = 10 × 52.1775/12 = 43.48125 → 43.48; yearly = 10 × 52.1775 = 521.775 → 521.78.
        // The fractional results exercise Round2 (AwayFromZero) — an exact-integer assertion would not.
        Assert.Equal(43.48m, summary.RunRate.ConvertedMonthly);
        Assert.Equal(521.78m, summary.RunRate.ConvertedYearly);
    }

    [Fact]
    public async Task NextBilling_DailyAndWeekly_HonourIntervalCountStep()
    {
        await using var context = TestContextFactory.Create();
        context.Subscriptions.AddRange(
            // Daily every 3 days, anchored 06-10 → first occurrence ≥ 06-15 is 06-16 (10 + ceil(5/3)*3).
            Sub(5m, "USD", ContextBillingInterval.Daily, new DateOnly(2026, 6, 10), intervalCount: 3, name: "Daily3"),
            // Weekly every 2 weeks, anchored 06-01 → 06-15 (14 days later, exactly today).
            Sub(5m, "USD", ContextBillingInterval.Weekly, new DateOnly(2026, 6, 1), intervalCount: 2, name: "Weekly2"));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        var byName = summary.UpcomingRenewals.ToDictionary(r => r.Name, r => r.NextBillingDate);
        Assert.Equal(new DateOnly(2026, 6, 16), byName["Daily3"]);
        Assert.Equal(new DateOnly(2026, 6, 15), byName["Weekly2"]);
    }

    [Fact]
    public async Task NextBilling_MonthEndAnchor_RecoversDayOfMonth_NoPermanentDrift()
    {
        await using var context = TestContextFactory.Create();
        // Anchored on Jan 31. Advancing month-by-month from a clamped result would drift to the 28th
        // after February; measuring from the anchor each step recovers 30/31 in longer months.
        context.Subscriptions.Add(Sub(9m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 31)));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        // From 01-31, the first occurrence ≥ 06-15 is 06-30 (June clamps 31→30), NOT the drifted 06-28.
        var renewal = Assert.Single(summary.UpcomingRenewals);
        Assert.Equal(new DateOnly(2026, 6, 30), renewal.NextBillingDate);
        Assert.Equal(15, renewal.DaysUntil);
    }

    // ── Renewal window / cap / ordering ──

    [Fact]
    public async Task UpcomingRenewals_HonoursWindowEdge()
    {
        await using var context = TestContextFactory.Create();
        context.Subscriptions.AddRange(
            Sub(5m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 7, 30), name: "In"),   // 06-15 + 45 = 07-30 (inside)
            Sub(5m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 7, 31), name: "Out")); // +46 (outside)
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        var renewal = Assert.Single(summary.UpcomingRenewals);
        Assert.Equal("In", renewal.Name);
        Assert.Equal(45, renewal.DaysUntil);
    }

    [Fact]
    public async Task UpcomingRenewals_CapAtSix_OrderedByDate()
    {
        await using var context = TestContextFactory.Create();
        // Seven subs billing on consecutive future days 06-16..06-22 (added out of order).
        var names = new[] { "G", "C", "A", "F", "B", "E", "D" };
        for (var i = 0; i < 7; i++)
        {
            context.Subscriptions.Add(Sub(1m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 6, 16 + i), name: names[i]));
        }
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        Assert.Equal(6, summary.UpcomingRenewals.Count); // capped
        var dates = summary.UpcomingRenewals.Select(r => r.NextBillingDate).ToList();
        Assert.Equal(dates.OrderBy(d => d).ToList(), dates); // ascending by date
        Assert.Equal(new DateOnly(2026, 6, 16), dates.First());
        Assert.Equal(new DateOnly(2026, 6, 21), dates.Last()); // the 06-22 sub is dropped by the cap
    }

    [Fact]
    public async Task UpcomingRenewals_SameDate_OrderedByName()
    {
        await using var context = TestContextFactory.Create();
        context.Subscriptions.AddRange(
            Sub(1m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 6, 18), name: "Zebra"),
            Sub(1m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 6, 18), name: "Apple"));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        Assert.Equal(new[] { "Apple", "Zebra" }, summary.UpcomingRenewals.Select(r => r.Name).ToArray());
    }

    [Fact]
    public async Task UpcomingRenewals_EndDateEqualToNextBilling_IsIncluded_ButBeforeIsExcluded()
    {
        await using var context = TestContextFactory.Create();
        context.Subscriptions.AddRange(
            // Next billing 06-20; endDate exactly 06-20 → still bills that day (included).
            Sub(5m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 6, 20), endDate: new DateOnly(2026, 6, 20), name: "OnEnd"),
            // Next billing 06-21; endDate 06-20 is before it → no further billing (excluded).
            Sub(5m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 6, 21), endDate: new DateOnly(2026, 6, 20), name: "PastEnd"));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        var renewal = Assert.Single(summary.UpcomingRenewals);
        Assert.Equal("OnEnd", renewal.Name);
        Assert.Equal(new DateOnly(2026, 6, 20), renewal.NextBillingDate);
    }

    [Fact]
    public async Task RunRate_TopDriver_ExcludesUnconvertibleCurrencies()
    {
        await using var context = TestContextFactory.Create();
        // A big nominal amount in a currency with NO rate to base must not win the driver over a real
        // (convertible) base-currency cost — it is not commensurate with the blended total.
        context.Subscriptions.AddRange(
            Sub(50m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), name: "RealTop"),
            Sub(5000m, "SEK", ContextBillingInterval.Monthly, new DateOnly(2026, 1, 10), name: "Unconvertible"));
        await context.SaveChangesAsync();

        var summary = await CreateService(context).GetSummary("USD");

        Assert.Equal("RealTop", summary.RunRate.TopDriver?.Name);
        Assert.Contains("SEK", summary.RunRate.ExcludedCurrencies);
    }

    // ── The three admin-editable summary limits (issue #437) ─────────────────────────────────────
    //
    // Everything above this line exercises the SEEDED defaults through the fake, which is AC 35: the
    // page is behaviourally identical at 45 days and six renewal rows. These assert that the values are
    // actually read from the settings store rather than from the constants they replaced.

    /// <summary>
    /// AC 2, the engine half. A window raised to 90 includes a renewal 60 days out, which the shipped
    /// 45-day window excluded — so the setting really does reach the computation, and the number the
    /// read DTO reports is the number the summary used.
    /// </summary>
    [Fact]
    public async Task RenewalWindow_ComesFromTheSettingsStore()
    {
        await using var context = TestContextFactory.Create();
        // 06-15 + 60 days = 08-14: outside the shipped 45-day window, inside a 90-day one.
        context.Subscriptions.Add(
            Sub(5m, "USD", ContextBillingInterval.Yearly, new DateOnly(2026, 8, 14), name: "SixtyOut"));
        await context.SaveChangesAsync();

        var atDefault = await CreateService(context).GetSummary("USD");
        Assert.Empty(atDefault.UpcomingRenewals);

        var widened = new FakeSystemSettingsLookup { Subscriptions = new SubscriptionSettings(90, 6, 1000) };
        var summary = await CreateService(context, widened).GetSummary("USD");

        var renewal = Assert.Single(summary.UpcomingRenewals);
        Assert.Equal("SixtyOut", renewal.Name);
        Assert.Equal(60, renewal.DaysUntil);
    }

    /// <summary>
    /// AC 3. With the cap lowered to two, a summary over five in-window subscriptions returns exactly
    /// the two EARLIEST by next billing date — the cap truncates the tail of the existing ordering
    /// rather than an arbitrary two.
    /// </summary>
    [Fact]
    public async Task RenewalCap_ComesFromTheSettingsStore_AndKeepsTheEarliest()
    {
        await using var context = TestContextFactory.Create();
        for (var i = 0; i < 5; i++)
        {
            context.Subscriptions.Add(Sub(
                1m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 6, 16 + i), name: $"S{i}"));
        }

        await context.SaveChangesAsync();

        var capped = new FakeSystemSettingsLookup { Subscriptions = new SubscriptionSettings(45, 2, 1000) };
        var summary = await CreateService(context, capped).GetSummary("USD");

        Assert.Equal(new[] { "S0", "S1" }, summary.UpcomingRenewals.Select(r => r.Name).ToArray());
    }

    /// <summary>
    /// AC 7. The bounded fetch reads exactly the cap, and <strong>which</strong> rows it reads is
    /// asserted rather than merely counted: non-archived before archived, newer <c>CreatedAtUtc</c>
    /// first, <c>SubscriptionId</c> breaking ties.
    ///
    /// <para>
    /// The ordering has to be pinned because a <c>Take()</c> over a non-deterministic order silently
    /// returns a different set per request. The PK tiebreak is what makes it deterministic on
    /// bulk-seeded or imported rows, where <c>CreatedAtUtc</c> alone ties.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheSummaryFetch_IsBoundedAndDeterministic()
    {
        await using var context = TestContextFactory.Create();

        // An archived row created most recently — it must still sort LAST, because archived-ness
        // outranks recency, so the cap discards it first.
        context.Subscriptions.Add(Sub(
            1m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 6, 17),
            archived: FixedNow, name: "ArchivedNewest", createdAt: FixedNow.AddDays(10)));

        // Three live rows sharing ONE CreatedAtUtc, so only the primary key separates them. Each has a
        // renewal inside the window, so the roll-up names whichever survived the cap.
        var tied = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var live = Enumerable.Range(0, 3)
            .Select(i => Sub(
                1m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 6, 18 + i),
                name: $"Live{i}", createdAt: tied))
            .ToList();
        context.Subscriptions.AddRange(live);
        await context.SaveChangesAsync();

        var capped = new FakeSystemSettingsLookup { Subscriptions = new SubscriptionSettings(45, 6, 2) };
        var summary = await CreateService(context, capped).GetSummary("USD");

        // Exactly the cap was read, and the archived row is not among them even though it is the newest.
        Assert.Equal(2, summary.Total);
        Assert.Equal(0, summary.CountsByStatus.Archived);

        // …and WHICH two is the PK order, not an arbitrary pair: with CreatedAtUtc tied, the two
        // smallest SubscriptionIds win. This is the assertion the tiebreak exists for — without it the
        // same Take() could return a different two on the next request.
        var expected = live
            .OrderBy(subscription => subscription.SubscriptionId)
            .Take(2)
            .Select(subscription => subscription.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expected,
            summary.UpcomingRenewals.Select(r => r.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// AC 8. The honest consequence of silent truncation, pinned: above the cap the status counts sum
    /// to the cap, <strong>and</strong> a renewal due inside the window on a truncated-away subscription
    /// is absent from the roll-up.
    ///
    /// <para>
    /// This is the way the subscriptions summary differs from its two siblings, which truncate a set
    /// feeding aggregates only. Here the same truncated set also builds a list of NAMED subscriptions,
    /// so above the cap a renewal due next week can silently vanish — a different class of wrongness
    /// from an approximate count.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AboveTheCap_CountsAreTruncated_AndANamedRenewalCanBeOmitted()
    {
        await using var context = TestContextFactory.Create();

        // Two newer rows fill the cap; the older one carries the in-window renewal.
        context.Subscriptions.AddRange(
            Sub(1m, "USD", ContextBillingInterval.Yearly, new DateOnly(2027, 1, 1),
                name: "NewerA", createdAt: FixedNow.AddDays(5)),
            Sub(1m, "USD", ContextBillingInterval.Yearly, new DateOnly(2027, 1, 1),
                name: "NewerB", createdAt: FixedNow.AddDays(4)),
            Sub(1m, "USD", ContextBillingInterval.Monthly, new DateOnly(2026, 6, 20),
                name: "OlderWithRenewal", createdAt: FixedNow.AddDays(-100)));
        await context.SaveChangesAsync();

        var uncapped = await CreateService(context).GetSummary("USD");
        Assert.Equal(3, uncapped.Total);
        Assert.Contains(uncapped.UpcomingRenewals, r => r.Name == "OlderWithRenewal");

        var capped = new FakeSystemSettingsLookup { Subscriptions = new SubscriptionSettings(45, 6, 2) };
        var summary = await CreateService(context, capped).GetSummary("USD");

        Assert.Equal(2, summary.Total);
        Assert.DoesNotContain(summary.UpcomingRenewals, r => r.Name == "OlderWithRenewal");
    }

    /// <summary>
    /// The lookup is a <strong>required</strong> constructor parameter, matching <c>InsuranceService</c>
    /// rather than this class's own optional <c>TimeProvider</c>/<c>CurrencyConversionService</c>: an
    /// optional null-defaulted lookup would let a direct construction silently revert to the compiled
    /// defaults the migration exists to remove.
    /// </summary>
    [Fact]
    public void TheSettingsLookup_IsARequiredConstructorParameter()
    {
        var parameter = typeof(SubscriptionService)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Single(p => p.ParameterType == typeof(ISystemSettingsLookup));

        Assert.False(parameter.IsOptional);
    }
}
