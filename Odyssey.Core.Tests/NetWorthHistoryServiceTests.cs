using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;
using AccountType = Odyssey.Context.AccountType;

namespace Odyssey.Core.Tests;

/// <summary>
/// The reconstruction rules (issue #90 §5): the grid, as-of balances, as-of estimates, as-of rates and
/// the fold, plus the four empty causes.
/// </summary>
/// <remarks>
/// Every test pins the clock. The final point's bound is <c>now</c>, so an ambient clock would make
/// the last point's contents depend on when the suite happened to run — the same reason the service
/// takes a <see cref="TimeProvider"/> at all.
/// </remarks>
public class NetWorthHistoryServiceTests
{
    private static readonly DateTime FixedNow = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private static NetWorthHistoryService Service(OdysseyContext context, DateTime? now = null) =>
        new(context, new CurrencyConversionService(context), new FixedTimeProvider(now ?? FixedNow));

    private static NetWorthHistoryQuery Query(
        NetWorthInterval interval = NetWorthInterval.Monthly,
        string? mainCurrency = "USD",
        DateOnly? from = null,
        DateOnly? to = null) =>
        new() { Interval = interval, MainCurrency = mainCurrency, From = from, To = to };

    private static Account NewAccount(
        Guid id,
        string name,
        AccountType type,
        string currency,
        DateTime opened,
        DateTime? archived = null) => new()
        {
            AccountId = id,
            Name = name,
            Description = name,
            Opened = opened,
            AccountType = type,
            CurrencyCode = currency,
            Archived = archived,
        };

    private static Transaction NewTransaction(Guid accountId, decimal amount, DateTime at) => new()
    {
        TransactionId = Guid.NewGuid(),
        Description = "tx",
        Amount = amount,
        TimeStamp = at,
        AccountId = accountId,
    };

    private static AccountEstimate NewEstimate(Guid accountId, decimal value, DateTime effectiveFrom, DateTime? created = null) => new()
    {
        AccountEstimateId = Guid.NewGuid(),
        AccountId = accountId,
        Value = value,
        CurrencyCode = "USD",
        EffectiveFrom = effectiveFrom,
        CreatedAtUtc = created ?? effectiveFrom,
    };

    private static ExchangeRate NewRate(string from, string to, decimal rate, DateTime asOf) => new()
    {
        FromCurrencyCode = from,
        ToCurrencyCode = to,
        Rate = rate,
        AsOf = asOf,
        CreatedAt = asOf,
    };

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    // ── AC29 / V4 — a point is dated at its period END ────────────────────────────────────────

    [Fact]
    public async Task A_point_is_dated_at_its_period_end_not_its_start()
    {
        await using var context = TestContextFactory.Create();
        var checking = Guid.NewGuid();
        context.Accounts.Add(NewAccount(checking, "Checking", AccountType.CheckingAccount, "USD",
            new DateTime(2024, 10, 3, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(
            Query(from: D(2024, 10, 1), to: D(2024, 12, 15)));

        // Net worth is a stock: the point covering October is true AT the end of October, so it is
        // dated 2024-11-01. Labelling it 2024-10-01 would carry the figure a whole period early.
        Assert.Equal(
            [D(2024, 11, 1), D(2024, 12, 1), D(2024, 12, 16)],
            history.Points.Select(point => point.Date));
    }

    [Fact]
    public async Task The_window_start_snaps_outward_to_its_period_start_and_is_echoed()
    {
        await using var context = TestContextFactory.Create();
        context.Accounts.Add(NewAccount(Guid.NewGuid(), "Checking", AccountType.CheckingAccount, "USD",
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(
            Query(from: D(2024, 10, 17), to: D(2024, 11, 20)));

        Assert.Equal(D(2024, 10, 1), history.From);
        Assert.Equal(D(2024, 11, 20), history.To);
    }

    // ── AC3 / AC4 — the series measures, it does not grow ─────────────────────────────────────

    [Fact]
    public async Task A_series_that_nets_down_decreases()
    {
        await using var context = TestContextFactory.Create();
        var checking = Guid.NewGuid();
        context.Accounts.Add(NewAccount(checking, "Checking", AccountType.CheckingAccount, "USD",
            new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.AddRange(
            NewTransaction(checking, 1000m, new DateTime(2025, 12, 5, 0, 0, 0, DateTimeKind.Utc)),
            NewTransaction(checking, -400m, new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc)),
            NewTransaction(checking, -300m, new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(
            Query(from: D(2025, 12, 1), to: D(2026, 2, 28)));

        Assert.Equal([1000m, 600m, 300m], history.Points.Select(point => point.NetWorth));
    }

    [Fact]
    public async Task A_negative_net_worth_is_reported_as_negative()
    {
        await using var context = TestContextFactory.Create();
        var mortgage = Guid.NewGuid();
        context.Accounts.Add(NewAccount(mortgage, "Mortgage", AccountType.Mortgage, "USD",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.Add(NewTransaction(mortgage, -827_700m, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query(from: D(2026, 1, 1), to: D(2026, 2, 28)));

        Assert.Equal(827_700m, history.Points[0].TotalLiabilities);
        Assert.Equal(-827_700m, history.Points[0].NetWorth);
    }

    [Fact]
    public async Task A_change_today_moves_only_points_at_or_after_it()
    {
        await using var context = TestContextFactory.Create();
        var checking = Guid.NewGuid();
        context.Accounts.Add(NewAccount(checking, "Checking", AccountType.CheckingAccount, "USD",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.Add(NewTransaction(checking, 100m, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var before = await Service(context).ComputeAsync(Query(from: D(2026, 1, 1), to: D(2026, 4, 30)));

        // Identical requests over unchanged data are identical: no clock, no randomness, no drift.
        var again = await Service(context).ComputeAsync(Query(from: D(2026, 1, 1), to: D(2026, 4, 30)));
        Assert.Equal(before.Points, again.Points);

        context.Transactions.Add(NewTransaction(checking, 50m, new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var after = await Service(context).ComputeAsync(Query(from: D(2026, 1, 1), to: D(2026, 4, 30)));

        // Points before March are untouched; March's own point (dated 2026-04-01) and later move.
        Assert.Equal(before.Points[0], after.Points[0]);
        Assert.Equal(before.Points[1], after.Points[1]);
        Assert.Equal(100m, after.Points[1].NetWorth);
        Assert.Equal(150m, after.Points[2].NetWorth);
        Assert.Equal(150m, after.Points[3].NetWorth);
    }

    // ── AC26 / V7 — the period boundary ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_transaction_at_the_exact_period_boundary_falls_in_the_later_period()
    {
        await using var context = TestContextFactory.Create();
        var checking = Guid.NewGuid();
        context.Accounts.Add(NewAccount(checking, "Checking", AccountType.CheckingAccount, "USD",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        // Exactly midnight on 1 February — the bound of January's point, which is exclusive.
        context.Transactions.Add(NewTransaction(checking, 500m, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query(from: D(2026, 1, 1), to: D(2026, 2, 28)));

        Assert.Equal(0m, history.Points[0].NetWorth);
        Assert.Equal(500m, history.Points[1].NetWorth);
    }

    // ── AC5 / AC31 / V8 — estimates and revaluation ───────────────────────────────────────────

    [Fact]
    public async Task An_estimate_affects_points_at_or_after_its_effective_date_and_none_before()
    {
        await using var context = TestContextFactory.Create();
        var house = Guid.NewGuid();
        context.Accounts.Add(NewAccount(house, "House", AccountType.Property, "USD",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.Add(NewTransaction(house, 5000m, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        context.AccountEstimates.Add(NewEstimate(house, 350_000m, new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query(from: D(2026, 1, 1), to: D(2026, 4, 30)));

        // Jan and Feb keep the transaction balance; March onwards takes the estimate.
        Assert.Equal([5000m, 5000m, 350_000m, 350_000m], history.Points.Select(point => point.NetWorth));
    }

    [Fact]
    public async Task A_period_in_which_an_estimate_took_effect_is_revalued_not_unconverted()
    {
        await using var context = TestContextFactory.Create();
        var house = Guid.NewGuid();
        context.Accounts.Add(NewAccount(house, "House", AccountType.Property, "USD",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.AccountEstimates.AddRange(
            NewEstimate(house, 300_000m, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc)),
            NewEstimate(house, 350_000m, new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query(from: D(2026, 1, 1), to: D(2026, 4, 30)));

        // March's point steps, and says why. The step is a real movement, so it must never be reported
        // as an understatement — those are opposites: one withholds the delta and the other does not.
        Assert.Equal(1, history.Points[2].RevaluedAccountCount);
        Assert.Equal(0, history.Points[2].UnconvertedAccountCount);

        // The first point is not "revalued": a step needs two points to be visible, and flagging the
        // first would mark every account that has ever carried an estimate.
        Assert.Equal(0, history.Points[0].RevaluedAccountCount);
        Assert.Equal(0, history.Points[1].RevaluedAccountCount);
        Assert.Equal(0, history.Points[3].RevaluedAccountCount);
    }

    [Fact]
    public async Task An_estimate_entered_later_does_not_rewrite_the_past()
    {
        await using var context = TestContextFactory.Create();
        var house = Guid.NewGuid();
        context.Accounts.Add(NewAccount(house, "House", AccountType.Property, "USD",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.Add(NewTransaction(house, 5000m, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        // Entered in April, effective from March: it applies from March forward, not from the start.
        context.AccountEstimates.Add(NewEstimate(house, 350_000m,
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            created: new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query(from: D(2026, 1, 1), to: D(2026, 3, 31)));

        Assert.Equal([5000m, 5000m, 350_000m], history.Points.Select(point => point.NetWorth));
    }

    [Fact]
    public async Task Two_estimates_effective_at_the_same_instant_are_tie_broken_by_creation()
    {
        await using var context = TestContextFactory.Create();
        var house = Guid.NewGuid();
        var effective = new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc);
        context.Accounts.Add(NewAccount(house, "House", AccountType.Property, "USD",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.AccountEstimates.AddRange(
            NewEstimate(house, 100_000m, effective, created: new DateTime(2026, 2, 3, 9, 0, 0, DateTimeKind.Utc)),
            NewEstimate(house, 250_000m, effective, created: new DateTime(2026, 2, 3, 17, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query(from: D(2026, 2, 1), to: D(2026, 2, 28)));

        Assert.Equal(250_000m, history.Points[0].NetWorth);
    }

    // ── AC6 / V6 — an account that has not opened yet ─────────────────────────────────────────

    [Fact]
    public async Task An_account_opened_after_a_points_bound_contributes_nothing_and_is_not_partial()
    {
        await using var context = TestContextFactory.Create();
        var early = Guid.NewGuid();
        var late = Guid.NewGuid();
        context.Accounts.AddRange(
            NewAccount(early, "Early", AccountType.CheckingAccount, "USD",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            // A currency with no rate to USD, so an account wrongly counted before it opened would
            // also be reported as unconvertible — the reading that calls a future account a defect.
            NewAccount(late, "Late", AccountType.CheckingAccount, "GBP",
                new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.AddRange(
            NewTransaction(early, 100m, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            NewTransaction(late, 900m, new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query(from: D(2026, 1, 1), to: D(2026, 2, 28)));

        Assert.All(history.Points, point =>
        {
            Assert.Equal(100m, point.NetWorth);
            Assert.Equal(0, point.UnconvertedAccountCount);
            Assert.Equal(1, point.ContributingAccountCount);
        });
        Assert.Empty(history.UnconvertedAccounts);
    }

    // ── AC7 / AC8 / AC9 / V9–V11 — rates ──────────────────────────────────────────────────────

    [Fact]
    public async Task Each_point_uses_the_rate_in_force_at_its_own_bound()
    {
        await using var context = TestContextFactory.Create();
        var savings = Guid.NewGuid();
        context.Accounts.Add(NewAccount(savings, "EUR Savings", AccountType.SavingsAccount, "EUR",
            new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.Add(NewTransaction(savings, 100m, new DateTime(2025, 12, 2, 0, 0, 0, DateTimeKind.Utc)));
        context.ExchangeRates.AddRange(
            NewRate("EUR", "USD", 1.0m, new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc)),
            NewRate("EUR", "USD", 2.0m, new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query(from: D(2025, 12, 1), to: D(2026, 3, 31)));

        // Dec (bound 2026-01-01) and Jan (bound 2026-02-01) predate the second rate; Feb and Mar use it.
        // Converting a December balance at February's rate would be exactly the kind of fabrication
        // this feature exists to remove.
        Assert.Equal([100m, 100m, 200m, 200m], history.Points.Select(point => point.NetWorth));
    }

    [Fact]
    public async Task An_account_whose_only_rate_is_later_is_counted_once_as_unconverted()
    {
        await using var context = TestContextFactory.Create();
        var zurich = Guid.NewGuid();
        var home = Guid.NewGuid();
        context.Accounts.AddRange(
            NewAccount(zurich, "Zurich brokerage", AccountType.InvestmentAccount, "CHF",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            NewAccount(home, "Checking", AccountType.CheckingAccount, "USD",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.AddRange(
            NewTransaction(zurich, 900m, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            NewTransaction(home, 50m, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        context.ExchangeRates.Add(NewRate("CHF", "USD", 1.1m, new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query(from: D(2026, 1, 1), to: D(2026, 4, 30)));

        // Jan and Feb are understated: the Zurich account contributed 0, and neither counts towards
        // ContributingAccountCount, so a wholly-understated point cannot look healthy.
        Assert.Equal(1, history.Points[0].UnconvertedAccountCount);
        Assert.Equal(1, history.Points[0].ContributingAccountCount);
        Assert.Equal(50m, history.Points[0].NetWorth);

        Assert.Equal(0, history.Points[2].UnconvertedAccountCount);
        Assert.Equal(2, history.Points[2].ContributingAccountCount);
        Assert.Equal(50m + (900m * 1.1m), history.Points[2].NetWorth);

        // Once in the roster, however many points it understated.
        var account = Assert.Single(history.UnconvertedAccounts);
        Assert.Equal("Zurich brokerage", account.Name);
    }

    [Fact]
    public async Task A_reverse_rate_does_not_satisfy_the_pair_and_there_is_no_two_hop_path()
    {
        await using var context = TestContextFactory.Create();
        var savings = Guid.NewGuid();
        context.Accounts.Add(NewAccount(savings, "EUR Savings", AccountType.SavingsAccount, "EUR",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.Add(NewTransaction(savings, 100m, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        // The wrong direction, plus a two-hop path that must not be walked.
        context.ExchangeRates.AddRange(
            NewRate("USD", "EUR", 0.9m, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            NewRate("EUR", "SEK", 11m, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            NewRate("SEK", "USD", 0.1m, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query(from: D(2026, 1, 1), to: D(2026, 2, 28)));

        Assert.All(history.Points, point => Assert.Equal(1, point.UnconvertedAccountCount));
        Assert.Single(history.UnconvertedAccounts);
    }

    [Fact]
    public async Task A_carry_in_rate_from_before_the_window_serves_the_first_point()
    {
        await using var context = TestContextFactory.Create();
        var savings = Guid.NewGuid();
        context.Accounts.Add(NewAccount(savings, "EUR Savings", AccountType.SavingsAccount, "EUR",
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.Add(NewTransaction(savings, 100m, new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        // The only rate row is years before the window. It still has to reach the first point.
        context.ExchangeRates.Add(NewRate("EUR", "USD", 1.5m, new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query(from: D(2026, 1, 1), to: D(2026, 2, 28)));

        Assert.All(history.Points, point =>
        {
            Assert.Equal(150m, point.NetWorth);
            Assert.Equal(0, point.UnconvertedAccountCount);
        });
    }

    // ── V12 / V13 — the classification ────────────────────────────────────────────────────────

    [Fact]
    public async Task Assets_liabilities_and_unknown_accounts_are_classified_as_the_totals_classify_them()
    {
        await using var context = TestContextFactory.Create();
        var cash = Guid.NewGuid();
        var card = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var opened = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        context.Accounts.AddRange(
            NewAccount(cash, "Cash", AccountType.Cash, "USD", opened),
            NewAccount(card, "Card", AccountType.CreditCard, "USD", opened),
            NewAccount(unknown, "Unclassified", AccountType.Unknown, "USD", opened));
        context.Transactions.AddRange(
            NewTransaction(cash, 1000m, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            NewTransaction(card, -250m, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            NewTransaction(unknown, 9999m, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query(from: D(2026, 1, 1), to: D(2026, 1, 31)));
        var point = Assert.Single(history.Points);

        Assert.Equal(1000m, point.TotalAssets);
        Assert.Equal(250m, point.TotalLiabilities);
        Assert.Equal(750m, point.NetWorth);
    }

    [Fact]
    public async Task An_archived_account_is_excluded_as_of_now_across_the_whole_series()
    {
        await using var context = TestContextFactory.Create();
        var live = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var opened = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        context.Accounts.AddRange(
            NewAccount(live, "Live", AccountType.CheckingAccount, "USD", opened),
            NewAccount(gone, "Archived", AccountType.CheckingAccount, "USD", opened,
                archived: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.AddRange(
            NewTransaction(live, 100m, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
            NewTransaction(gone, 900m, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query(from: D(2026, 1, 1), to: D(2026, 2, 28)));

        // As-of-now membership is a choice, made so the last point can equal /totals. The accepted
        // consequence is exactly this: archiving one account retroactively moves the whole line.
        Assert.All(history.Points, point => Assert.Equal(100m, point.NetWorth));
    }

    // ── AC11 / V14 — the four empty causes ────────────────────────────────────────────────────

    [Fact]
    public async Task No_accounts_yields_NoAccounts()
    {
        await using var context = TestContextFactory.Create();

        var history = await Service(context).ComputeAsync(Query());

        Assert.Empty(history.Points);
        Assert.Equal(NetWorthEmptyReason.NoAccounts, history.EmptyReason);
    }

    [Fact]
    public async Task A_window_ending_before_the_first_account_yields_WindowBeforeFirstAccount()
    {
        await using var context = TestContextFactory.Create();
        context.Accounts.Add(NewAccount(Guid.NewGuid(), "Checking", AccountType.CheckingAccount, "USD",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(
            Query(from: D(2020, 1, 1), to: D(2020, 6, 30)));

        Assert.Empty(history.Points);
        Assert.Equal(NetWorthEmptyReason.WindowBeforeFirstAccount, history.EmptyReason);
    }

    [Fact]
    public async Task Nothing_convertible_yields_NothingConvertible_rather_than_a_line_of_zeroes()
    {
        await using var context = TestContextFactory.Create();
        var zurich = Guid.NewGuid();
        context.Accounts.Add(NewAccount(zurich, "Zurich brokerage", AccountType.InvestmentAccount, "CHF",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.Add(NewTransaction(zurich, 900m, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query(from: D(2026, 1, 1), to: D(2026, 3, 31)));

        // A line of zeroes would read as "you were worth nothing", which is the opposite of
        // "we could not tell".
        Assert.Empty(history.Points);
        Assert.Equal(NetWorthEmptyReason.NothingConvertible, history.EmptyReason);
        Assert.Single(history.UnconvertedAccounts);
    }

    [Fact]
    public async Task Leading_empty_periods_are_clamped_away_rather_than_plotted_as_zero()
    {
        await using var context = TestContextFactory.Create();
        context.Accounts.Add(NewAccount(Guid.NewGuid(), "Checking", AccountType.CheckingAccount, "USD",
            new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query(from: D(2025, 1, 1), to: D(2026, 4, 30)));

        // A flat run at 0 before any account existed is a claim about net worth, and a false one.
        Assert.Equal(D(2026, 3, 1), history.From);
        Assert.Equal(2, history.Points.Count);
    }

    // ── AC22 — grid boundaries per interval ───────────────────────────────────────────────────

    [Fact]
    public void Weekly_periods_start_on_monday()
    {
        // 2026-09-16 is a Wednesday; its ISO week starts on Monday the 14th.
        Assert.Equal(D(2026, 9, 14), NetWorthPeriods.StartOfPeriod(D(2026, 9, 16), NetWorthInterval.Weekly));
        // A Sunday belongs to the week that began the previous Monday, not to the one starting next day.
        Assert.Equal(D(2026, 9, 14), NetWorthPeriods.StartOfPeriod(D(2026, 9, 20), NetWorthInterval.Weekly));
        Assert.Equal(D(2026, 9, 21), NetWorthPeriods.StartOfPeriod(D(2026, 9, 21), NetWorthInterval.Weekly));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 4)]
    [InlineData(9, 7)]
    [InlineData(12, 10)]
    public void Quarterly_periods_start_on_a_calendar_quarter(int month, int expectedMonth)
    {
        Assert.Equal(D(2026, expectedMonth, 1), NetWorthPeriods.StartOfPeriod(D(2026, month, 14), NetWorthInterval.Quarterly));
    }

    [Fact]
    public void Periods_are_counted_by_calendar_not_by_day_arithmetic()
    {
        // February 2024 has 29 days. Counting days and dividing would make this window a different
        // number of months from the same window in a non-leap year.
        Assert.Equal(3, NetWorthPeriods.CountPeriods(D(2024, 1, 15), D(2024, 3, 2), NetWorthInterval.Monthly));
        Assert.Equal(366, NetWorthPeriods.CountPeriods(D(2024, 1, 1), D(2024, 12, 31), NetWorthInterval.Daily));
        Assert.Equal(365, NetWorthPeriods.CountPeriods(D(2025, 1, 1), D(2025, 12, 31), NetWorthInterval.Daily));
        Assert.Equal(2, NetWorthPeriods.CountPeriods(D(2024, 12, 31), D(2025, 1, 1), NetWorthInterval.Yearly));
        Assert.Equal(1, NetWorthPeriods.CountPeriods(D(2026, 9, 14), D(2026, 9, 20), NetWorthInterval.Weekly));
        Assert.Equal(2, NetWorthPeriods.CountPeriods(D(2026, 9, 14), D(2026, 9, 21), NetWorthInterval.Weekly));
    }

    [Fact]
    public async Task A_yearly_series_walks_calendar_years()
    {
        await using var context = TestContextFactory.Create();
        var checking = Guid.NewGuid();
        context.Accounts.Add(NewAccount(checking, "Checking", AccountType.CheckingAccount, "USD",
            new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.AddRange(
            NewTransaction(checking, 100m, new DateTime(2023, 7, 1, 0, 0, 0, DateTimeKind.Utc)),
            NewTransaction(checking, 100m, new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc)),
            NewTransaction(checking, 100m, new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(
            Query(NetWorthInterval.Yearly, from: D(2023, 1, 1), to: D(2025, 12, 31)));

        Assert.Equal([D(2024, 1, 1), D(2025, 1, 1), D(2026, 1, 1)], history.Points.Select(point => point.Date));
        Assert.Equal([100m, 200m, 300m], history.Points.Select(point => point.NetWorth));
    }

    // ── AC35 — every interval is capped ───────────────────────────────────────────────────────

    [Fact]
    public void Every_interval_resolves_to_a_cap()
    {
        // The resolver is an exhaustive switch with no default arm, so the real guard is the compiler
        // (the build runs at zero warnings, and CS8509 is a missing named member). This is the runtime
        // half: it also proves no cap is zero or negative, which would make the interval unusable
        // rather than uncapped.
        foreach (var interval in Enum.GetValues<NetWorthInterval>())
        {
            Assert.True(NetWorthHistoryQuery.PointCapFor(interval) > 0,
                $"{interval} has no usable point cap.");
        }
    }

    // ── V1 — currency validation ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unsupported_main_currency_is_rejected()
    {
        await using var context = TestContextFactory.Create();

        await Assert.ThrowsAsync<DomainValidationException>(
            () => Service(context).ComputeAsync(Query(mainCurrency: "ZZZ")));
    }

    // ── AC2's premise — the final point is measured at `now` ───────────────────────────────────

    [Fact]
    public async Task The_final_point_of_a_series_ending_today_is_measured_at_now()
    {
        await using var context = TestContextFactory.Create();
        var checking = Guid.NewGuid();
        context.Accounts.Add(NewAccount(checking, "Checking", AccountType.CheckingAccount, "USD",
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.AddRange(
            NewTransaction(checking, 100m, FixedNow.AddHours(-1)),
            NewTransaction(checking, 500m, FixedNow.AddHours(1)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query());
        var last = history.Points[^1];

        Assert.Equal(DateOnly.FromDateTime(FixedNow), last.Date);
        // The later row is in the same calendar period but after `now`, so it is out — which is what
        // makes this point equal to what /accounts/totals reports for the same instant.
        Assert.Equal(100m, last.NetWorth);
    }

    [Fact]
    public async Task The_default_window_is_twenty_four_monthly_points()
    {
        await using var context = TestContextFactory.Create();
        context.Accounts.Add(NewAccount(Guid.NewGuid(), "Checking", AccountType.CheckingAccount, "USD",
            new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var history = await Service(context).ComputeAsync(Query());

        Assert.Equal(NetWorthHistoryQuery.DefaultPoints, history.Points.Count);
        Assert.Equal(NetWorthInterval.Monthly, history.Interval);
        Assert.Equal(D(2024, 7, 1), history.From);
    }
}
