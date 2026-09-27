using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Finance;
using Xunit;
using AccountType = Odyssey.Context.AccountType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine coverage for the net-worth history (issue #90 AC15, AC16, AC21, AC26, AC36 and §12), and
/// for property value in it (issue #214 AC3, AC16, AC22).
/// </summary>
/// <remarks>
/// <para>
/// Four things here are invisible to the fast tiers by construction. EF InMemory issues no SQL, so
/// the round-trip count cannot be observed and the bucketed aggregate is executed in LINQ-to-objects
/// rather than by the database — the decimal fidelity question does not even arise. It has no schema,
/// so the index migration is unobservable. And it compares full <see cref="DateTime"/> ticks where
/// MariaDB's <c>datetime(6)</c> truncates to microseconds, which is exactly the divergence the
/// exclusive period bound exists to avoid.
/// </para>
/// <para>
/// The last one is worth stating plainly: a test written against an inclusive
/// <c>23:59:59.9999999</c> bound <b>passes</b> on the Core tier and differs on MariaDB, so the fast
/// tier cannot be the place that decides it.
/// </para>
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class NetWorthHistoryIntegrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_net_worth_history";

    private static readonly DateTime FixedNow = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    // ── AC21 — exactly one migration, and it swaps the index rather than adding one ────────────

    [SkippableFact]
    public async Task TheIndexMigration_DropsTheTwoColumnIndexAndCreatesTheCoveringOne()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using var context = new OdysseyContext(options);
        var indexes = await IndexNamesAsync(context, "Transactions");

        Assert.Contains("IX_Transactions_AccountId_TimeStamp_Amount", indexes);

        // The old index is a strict PREFIX of the new one, so keeping both would cost an extra
        // secondary-index write on every insert to the highest-volume table for no read benefit.
        Assert.DoesNotContain("IX_Transactions_AccountId_TimeStamp", indexes);

        // And the standalone AccountId index stays gone, as it has been since the composite replaced it.
        Assert.DoesNotContain("IX_Transactions_AccountId", indexes);
    }

    [SkippableFact]
    public async Task TheCoveringIndex_CarriesItsThreeColumnsInOrder()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using var context = new OdysseyContext(options);
        var columns = new List<string>();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COLUMN_NAME
                FROM INFORMATION_SCHEMA.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME = 'Transactions'
                  AND INDEX_NAME = 'IX_Transactions_AccountId_TimeStamp_Amount'
                ORDER BY SEQ_IN_INDEX
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }
        }

        await connection.CloseAsync();

        // Amount last: the leading pair is what the list query filters and orders on, and Amount is
        // there only so the history's aggregate is answered from the index instead of the heap.
        Assert.Equal(["AccountId", "TimeStamp", "Amount"], columns);
    }

    // ── AC15, #214 AC16 — seven round trips with properties, five without, whatever the point count

    [SkippableTheory]
    [InlineData(NetWorthInterval.Monthly, 2)]
    [InlineData(NetWorthInterval.Monthly, NetWorthHistoryQuery.MaxPoints)]
    [InlineData(NetWorthInterval.Daily, NetWorthHistoryQuery.MaxDailyPoints)]
    [InlineData(NetWorthInterval.Weekly, NetWorthHistoryQuery.MaxWeeklyPoints)]
    [InlineData(NetWorthInterval.Quarterly, NetWorthHistoryQuery.MaxPoints)]
    [InlineData(NetWorthInterval.Yearly, NetWorthHistoryQuery.MaxPoints)]
    public async Task TheComputation_CostsSevenRoundTripsWithProperties_AndFiveWithout_RegardlessOfPointCount(
        NetWorthInterval interval, int points)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var counter = new CommandCounter();
        var options = await MigratedSchemaAsync(counter);

        await using var context = new OdysseyContext(options);
        await SeedPortfolioAsync(context);
        await SeedPropertyAsync(context, "GBP");

        var query = new NetWorthHistoryQuery
        {
            MainCurrency = "USD",
            Interval = interval,
            From = NetWorthPeriods.AddSaturating(DateOnly.FromDateTime(FixedNow), interval, -(points - 1)),
            To = DateOnly.FromDateTime(FixedNow),
        };

        counter.Reset();
        var accountsOnly = await ServiceFor(context).ComputeAsync(query, includeProperties: false);

        Assert.NotEmpty(accountsOnly.Points);

        // The currency check, the accounts, the bucketed aggregate, the estimates and the rate
        // timeline. A per-point query would make a 120-point request 120 times this.
        Assert.Equal(5, counter.Count);

        counter.Reset();
        var withProperties = await ServiceFor(context).ComputeAsync(query, includeProperties: true);

        Assert.NotEmpty(withProperties.Points);

        // Plus the properties and their estimates. The rate timeline stays ONE query over the union of
        // account and property currencies — GBP is a property-only currency here, so a second rate
        // query for it would make this eight.
        Assert.Equal(7, counter.Count);
    }

    // ── #214 AC3 — no property table is queried for an unentitled caller (command text) ───────

    [SkippableFact]
    public async Task NotIncluded_NoCommandReferencesAPropertyTable_AndIncludedDoes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var counter = new CommandCounter();
        var options = await MigratedSchemaAsync(counter);

        await using var context = new OdysseyContext(options);
        await SeedPortfolioAsync(context);
        await SeedPropertyAsync(context, "USD");

        var totalsService = new AccountTotalsService(
            context, new CurrencyConversionService(context), new FixedTimeProvider(FixedNow));
        var query = new NetWorthHistoryQuery { MainCurrency = "USD", Interval = NetWorthInterval.Monthly };

        counter.Reset();
        await totalsService.ComputeAsync("USD", includeProperties: false);
        await ServiceFor(context).ComputeAsync(query, includeProperties: false);

        Assert.NotEmpty(counter.Texts);
        Assert.DoesNotContain(counter.Texts, text => text.Contains("`Properties`", StringComparison.Ordinal));
        Assert.DoesNotContain(counter.Texts, text => text.Contains("`PropertyEstimates`", StringComparison.Ordinal));

        // The positive control, on the same fixture: the interceptor sees these tables when they ARE read.
        foreach (var run in new Func<Task>[]
                 {
                     () => totalsService.ComputeAsync("USD", includeProperties: true),
                     () => ServiceFor(context).ComputeAsync(query, includeProperties: true),
                 })
        {
            counter.Reset();
            await run();
            Assert.Contains(counter.Texts, text => text.Contains("`Properties`", StringComparison.Ordinal));
            Assert.Contains(counter.Texts, text => text.Contains("`PropertyEstimates`", StringComparison.Ordinal));
        }
    }

    // ── #214 AC22 — property boundaries on datetime(6) ────────────────────────────────────────

    /// <summary>
    /// An estimate stamped exactly at a period bound is not yet in force at it, one a microsecond
    /// earlier is, and a property disposed of exactly on a bound is already gone by it — through real
    /// SQL, where <c>datetime(6)</c> truncates and the nullable <c>OR</c>s are translated rather than
    /// evaluated in LINQ-to-objects. The final point also equals <c>/totals</c> with properties in it.
    /// </summary>
    [SkippableFact]
    public async Task PropertyBoundaries_HoldOnTheRealEngine()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        var march = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var april = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var seed = new OdysseyContext(options))
        {
            var house = Property("House", "USD", acquired: null, disposed: null);
            var car = Property("Car", "USD", acquired: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), disposed: march);
            seed.Properties.AddRange(house, car);
            seed.PropertyEstimates.AddRange(
                Estimate(house, 100m, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                Estimate(house, 200m, march),                          // exactly on the Mar 1 bound
                Estimate(house, 300m, april.AddTicks(-10)),            // one microsecond before Apr 1
                Estimate(car, 10_000m, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            await seed.SaveChangesAsync();
        }

        await using var context = new OdysseyContext(options);
        var history = await ServiceFor(context).ComputeAsync(new NetWorthHistoryQuery
        {
            MainCurrency = "USD",
            Interval = NetWorthInterval.Monthly,
            From = new DateOnly(2026, 1, 1),
            To = new DateOnly(2026, 4, 30),
        }, includeProperties: true);

        // Feb 1: house 100 + car. Mar 1: the 200 row is AT the bound, so still 100; the car was disposed
        // AT the bound, so gone. Apr 1: the 300 row a microsecond before the bound is in force.
        Assert.Equal([10_100m, 100m, 300m, 300m], history.Points.Select(point => point.PropertyValue!.Value));
        Assert.Equal([2, 1, 1, 1], history.Points.Select(point => point.ContributingPropertyCount));

        var totals = await new AccountTotalsService(
            context, new CurrencyConversionService(context), new FixedTimeProvider(FixedNow))
            .ComputeAsync("USD", includeProperties: true);
        var full = await ServiceFor(context).ComputeAsync(
            new NetWorthHistoryQuery { MainCurrency = "USD", Interval = NetWorthInterval.Monthly }, includeProperties: true);

        Assert.Equal(totals.PropertyValue, full.Points[^1].PropertyValue);
        Assert.Equal(totals.NetWorth, full.Points[^1].NetWorth);
    }

    // ── AC16 — decimal fidelity of the bucketed aggregate ─────────────────────────────────────

    [SkippableFact]
    public async Task TheBucketedAggregate_MatchesARowByRowComputation()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using var context = new OdysseyContext(options);

        var accountId = Guid.NewGuid();
        context.Accounts.Add(NewAccount(accountId, "Checking", AccountType.CheckingAccount, "USD",
            FixedNow.AddYears(-3)));

        // Amount is decimal(18,6). Values chosen so a float-backed SUM would drift visibly and a
        // per-bucket partial sum could round differently from a single running total.
        var random = new Random(90);
        var expected = 0m;
        for (var i = 0; i < 600; i++)
        {
            var amount = Math.Round((decimal)((random.NextDouble() - 0.4) * 10_000), 6);
            expected += amount;
            context.Transactions.Add(NewTransaction(accountId, amount, FixedNow.AddDays(-700 + i)));
        }

        await context.SaveChangesAsync();

        var history = await ServiceFor(context).ComputeAsync(new NetWorthHistoryQuery
        {
            MainCurrency = "USD",
            Interval = NetWorthInterval.Monthly,
            From = DateOnly.FromDateTime(FixedNow.AddYears(-3)),
            To = DateOnly.FromDateTime(FixedNow),
        }, includeProperties: false);

        // Exact equality, not a tolerance: the whole point of decimal here is that the bucketed
        // aggregate plus its prefix sum is the same number as the row-by-row total.
        Assert.Equal(expected, history.Points[^1].NetWorth);
    }

    // ── AC26 — the period boundary, on the engine that truncates to microseconds ───────────────

    [SkippableFact]
    public async Task ATransactionAtTheExactPeriodBoundary_FallsInTheLaterPeriod()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using var context = new OdysseyContext(options);
        var accountId = Guid.NewGuid();
        context.Accounts.Add(NewAccount(accountId, "Checking", AccountType.CheckingAccount, "USD",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        context.Transactions.AddRange(
            // Exactly midnight on 1 March — January's and February's bounds are exclusive, so this
            // belongs to March.
            NewTransaction(accountId, 500m, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
            // One microsecond before it: the last instant MariaDB's datetime(6) can represent inside
            // February. An INCLUSIVE end-of-period bound would have to be expressed as
            // 23:59:59.9999999, whose 7th digit the driver truncates — so it would compare equal to
            // this row on MariaDB and not on EF InMemory.
            NewTransaction(accountId, 7m, new DateTime(2026, 2, 28, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_999_990)));
        await context.SaveChangesAsync();

        var history = await ServiceFor(context).ComputeAsync(new NetWorthHistoryQuery
        {
            MainCurrency = "USD",
            Interval = NetWorthInterval.Monthly,
            From = new DateOnly(2026, 1, 1),
            To = new DateOnly(2026, 3, 31),
        }, includeProperties: false);

        Assert.Equal(0m, history.Points[0].NetWorth);   // January
        Assert.Equal(7m, history.Points[1].NetWorth);   // February
        Assert.Equal(507m, history.Points[2].NetWorth); // March
    }

    // ── AC36 — the rate timeline is bounded by the query, not trimmed afterwards ───────────────

    [SkippableFact]
    public async Task TheRateTimeline_ReturnsInWindowRowsPlusOneCarryInPerCurrency()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        await using var context = new OdysseyContext(options);

        var windowStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 10 000 rows before the window, per currency. An in-memory trim would ship every one of them
        // across the wire and hold them all at peak — which was the concern, not the query count.
        var rows = new List<ExchangeRate>(20_010);
        foreach (var code in new[] { "EUR", "CHF" })
        {
            for (var i = 0; i < 10_000; i++)
            {
                rows.Add(NewRate(code, "USD", 1m + (i / 100_000m), windowStart.AddDays(-10_001 + i)));
            }

            // Three inside the window.
            rows.Add(NewRate(code, "USD", 2m, windowStart.AddDays(10)));
            rows.Add(NewRate(code, "USD", 3m, windowStart.AddDays(40)));
            rows.Add(NewRate(code, "USD", 4m, windowStart.AddDays(70)));
        }

        context.ExchangeRates.AddRange(rows);
        await context.SaveChangesAsync();

        var stopwatch = Stopwatch.StartNew();
        var timeline = await new CurrencyConversionService(context).GetRateTimelineToAsync(
            "USD", ["EUR", "CHF"], windowStart, windowStart.AddDays(100));
        stopwatch.Stop();

        // The bound is a REGRESSION GUARD, not a benchmark, and the gap it watches is enormous. The
        // shipped shape — a join to a grouped MAX, which the (From, To, AsOf) index answers with a
        // loose index scan — runs this in under a millisecond. The correlated-subquery form it
        // replaced took 23.6 s on this exact dataset (and a correlated MAX 52.8 s), because MariaDB
        // re-executes a dependent subquery once per candidate row. That is past MySqlConnector's 30 s
        // command timeout, so the symptom was not "slow" but a failed query reading "Query execution
        // was interrupted" — which is why this is asserted rather than left to a reviewer's eye.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"The rate timeline took {stopwatch.Elapsed.TotalSeconds:0.00}s over {rows.Count} rows. "
            + "A correlated subquery per candidate row is the regression this watches for.");

        foreach (var code in new[] { "EUR", "CHF" })
        {
            var points = timeline[code];

            // Three in-window rows plus exactly one carry-in, out of 10 003 candidates.
            Assert.Equal(4, points.Count);
            Assert.Equal(windowStart.AddDays(-2), points[0].AsOf);
            Assert.Equal([2m, 3m, 4m], points.Skip(1).Select(point => point.Rate));
        }
    }

    // ── §12 — the performance targets, and the peak the reconstruction holds ───────────────────

    [SkippableFact]
    public async Task OneHundredThousandTransactions_StayWithinTheStatedBudget()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var counter = new CommandCounter();
        var options = await MigratedSchemaAsync(counter);

        await using var context = new OdysseyContext(options);
        await SeedPortfolioAsync(context);
        await BulkInsertTransactionsAsync(context, 100_000);

        var service = ServiceFor(context);
        var query = new NetWorthHistoryQuery
        {
            MainCurrency = "USD",
            Interval = NetWorthInterval.Daily,
            From = NetWorthPeriods.AddSaturating(
                DateOnly.FromDateTime(FixedNow), NetWorthInterval.Daily, -(NetWorthHistoryQuery.MaxDailyPoints - 1)),
            To = DateOnly.FromDateTime(FixedNow),
        };

        // One warm run so the measurement is not dominated by first-query plan building.
        await service.ComputeAsync(query, includeProperties: false);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        counter.Reset();
        var stopwatch = Stopwatch.StartNew();
        var history = await service.ComputeAsync(query, includeProperties: false);
        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.NotEmpty(history.Points);
        Assert.Equal(5, counter.Count);

        // §12's p95 for this shape is 1 s. The assertion is deliberately looser than the target: this
        // runs inside a CI container against a containerised database, so it is a regression guard on
        // the ORDER of magnitude — a per-point query would blow through it — not a benchmark.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"The reconstruction took {stopwatch.Elapsed.TotalSeconds:0.00}s over 100 000 transactions.");

        // Peak working set is the other half of §12: the aggregate returns one row per
        // (account, bucket-with-activity), so materialising 100 000 raw rows would be visible here.
        Assert.True(allocated < 128L * 1024 * 1024,
            $"The reconstruction allocated {allocated / (1024 * 1024)} MB; the aggregate should return "
            + "buckets, not rows.");
    }

    // ── Issue #99 — the open/closed term, on the engine that actually stores the columns ──────

    /// <summary>
    /// The membership rule through both endpoints, against real SQL: an archived account still counts,
    /// a closed one does not, and an account closed at <b>exactly</b> the measuring instant is already
    /// gone by it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fast tiers cannot decide this one. <c>Closed</c> is nullable, so the predicate is a
    /// three-valued <c>OR</c> that EF must translate rather than evaluate in LINQ-to-objects — and the
    /// equality case is stored through <c>datetime(6)</c>, which truncates where EF InMemory compares
    /// full ticks. A boundary written at tick resolution passes on the Core tier and means something
    /// else here.
    /// </para>
    /// <para>
    /// It also pins issue #90 AC2 with a closed account in the portfolio: the final point and
    /// <c>/totals</c> share one instant, and both now resolve the term at it, so they agree
    /// structurally rather than by two predicates being kept in step.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task TheOpenClosedTerm_DecidesMembershipOnBothEndpoints()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var options = await MigratedSchemaAsync();

        var opened = FixedNow.AddYears(-2);
        var live = Guid.NewGuid();
        var filed = Guid.NewGuid();
        var closedLongAgo = Guid.NewGuid();
        var closedAtNow = Guid.NewGuid();

        await using (var seed = new OdysseyContext(options))
        {
            seed.Accounts.AddRange(
                NewAccount(live, "Live", AccountType.CheckingAccount, "USD", opened),
                NewAccount(filed, "Filed away", AccountType.SavingsAccount, "USD", opened,
                    archived: FixedNow.AddDays(-1)),
                NewAccount(closedLongAgo, "Closed", AccountType.CheckingAccount, "USD", opened,
                    closed: FixedNow.AddMonths(-6)),
                NewAccount(closedAtNow, "Closed at the measuring instant", AccountType.CheckingAccount,
                    "USD", opened, closed: FixedNow));

            seed.Transactions.AddRange(
                NewTransaction(live, 100m, opened.AddDays(1)),
                NewTransaction(filed, 200m, opened.AddDays(1)),
                NewTransaction(closedLongAgo, 4_000m, opened.AddDays(1)),
                NewTransaction(closedAtNow, 8_000m, opened.AddDays(1)));

            await seed.SaveChangesAsync();
        }

        await using var context = new OdysseyContext(options);

        var totals = await new AccountTotalsService(
            context, new CurrencyConversionService(context), new FixedTimeProvider(FixedNow))
            .ComputeAsync("USD", includeProperties: false);

        // Live + archived only. Archiving is a list filter; closing ends the term, and `Closed == now`
        // is outside it because the bound is exclusive at both ends.
        Assert.Equal(300m, totals.NetWorth);
        Assert.Empty(totals.UnconvertedAccounts);

        var history = await ServiceFor(context).ComputeAsync(
            new NetWorthHistoryQuery { MainCurrency = "USD", Interval = NetWorthInterval.Monthly }, includeProperties: false);

        // The per-point half: each account leaves the line at its own close date and keeps its past, so
        // the series steps down 12 300 → 8 300 → 300 rather than being flat at the final figure. The
        // last step happens only at the final bound, where `Closed == now` falls outside the term.
        Assert.Equal(12_300m, history.Points[0].NetWorth);
        Assert.Contains(history.Points, point => point.NetWorth == 8_300m);
        Assert.Equal(300m, history.Points[^1].NetWorth);
        Assert.Equal(totals.NetWorth, history.Points[^1].NetWorth);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private static NetWorthHistoryService ServiceFor(OdysseyContext context) =>
        new(context, new CurrencyConversionService(context), new FixedTimeProvider(FixedNow));

    private static async Task SeedPortfolioAsync(OdysseyContext context)
    {
        var opened = FixedNow.AddYears(-11);
        var checking = Guid.NewGuid();
        var savings = Guid.NewGuid();
        var house = Guid.NewGuid();
        var card = Guid.NewGuid();

        context.Accounts.AddRange(
            NewAccount(checking, "Checking", AccountType.CheckingAccount, "USD", opened),
            NewAccount(savings, "EUR Savings", AccountType.SavingsAccount, "EUR", opened),
            NewAccount(house, "House", AccountType.Property, "USD", opened),
            NewAccount(card, "Card", AccountType.CreditCard, "USD", opened));

        context.Transactions.AddRange(
            NewTransaction(checking, 25_000m, opened.AddDays(30)),
            NewTransaction(savings, 9_000m, opened.AddDays(60)),
            NewTransaction(card, -1_500m, opened.AddDays(90)),
            NewTransaction(house, 1m, opened.AddDays(120)));

        context.AccountEstimates.Add(new AccountEstimate
        {
            AccountEstimateId = Guid.NewGuid(),
            AccountId = house,
            Value = 4_500_000m,
            CurrencyCode = "USD",
            EffectiveFrom = opened.AddYears(1),
            CreatedAtUtc = opened.AddYears(1),
        });

        context.ExchangeRates.Add(NewRate("EUR", "USD", 1.09m, opened.AddDays(1)));

        await context.SaveChangesAsync();
    }

    /// <summary>A held property with one estimate in force, in <paramref name="currency"/>.</summary>
    private static async Task SeedPropertyAsync(OdysseyContext context, string currency)
    {
        var property = Property("Cabin", currency, acquired: FixedNow.AddYears(-5), disposed: null);
        context.Properties.Add(property);
        context.PropertyEstimates.Add(Estimate(property, 750_000m, FixedNow.AddYears(-5)));
        await context.SaveChangesAsync();
    }

    private static Property Property(string name, string currency, DateTime? acquired, DateTime? disposed) => new()
    {
        PropertyId = Guid.NewGuid(),
        Name = name,
        Description = name,
        Type = PropertyType.RealEstate,
        CurrencyCode = currency,
        AcquiredDate = acquired,
        DisposedDate = disposed,
        CreatedAt = FixedNow,
        UpdatedAt = FixedNow,
        RealEstateDetails = new RealEstateDetails { Kind = RealEstateKind.House },
    };

    private static PropertyEstimate Estimate(Property property, decimal value, DateTime effectiveFrom) => new()
    {
        PropertyEstimateId = Guid.NewGuid(),
        PropertyId = property.PropertyId,
        Value = value,
        CurrencyCode = property.CurrencyCode,
        EffectiveFrom = effectiveFrom,
        CreatedAtUtc = effectiveFrom,
    };

    /// <summary>
    /// 100 000 rows, inserted in batches with change tracking cleared between them — tracking them all
    /// would measure EF's change tracker rather than the query.
    /// </summary>
    private static async Task BulkInsertTransactionsAsync(OdysseyContext context, int count)
    {
        var accountIds = await context.Accounts.Select(account => account.AccountId).ToListAsync();
        var random = new Random(9001);
        const int batchSize = 5_000;

        for (var written = 0; written < count; written += batchSize)
        {
            for (var i = 0; i < batchSize && written + i < count; i++)
            {
                context.Transactions.Add(NewTransaction(
                    accountIds[random.Next(accountIds.Count)],
                    Math.Round((decimal)((random.NextDouble() - 0.5) * 2_000), 6),
                    FixedNow.AddDays(-random.Next(1, 3_650)).AddMinutes(-random.Next(1, 1_440))));
            }

            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }
    }

    private static Account NewAccount(
        Guid id,
        string name,
        AccountType type,
        string currency,
        DateTime opened,
        DateTime? closed = null,
        DateTime? archived = null) => new()
    {
        AccountId = id,
        Name = name,
        Description = name,
        Opened = opened,
        AccountType = type,
        CurrencyCode = currency,
        Closed = closed,
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

    private static ExchangeRate NewRate(string from, string to, decimal rate, DateTime asOf) => new()
    {
        FromCurrencyCode = from,
        ToCurrencyCode = to,
        Rate = rate,
        AsOf = asOf,
        CreatedAt = asOf,
    };

    private static async Task<List<string>> IndexNamesAsync(OdysseyContext context, string table)
    {
        var names = new List<string>();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT DISTINCT INDEX_NAME
                FROM INFORMATION_SCHEMA.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@table";
            parameter.Value = table;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }

        await connection.CloseAsync();
        return names;
    }

    /// <summary>
    /// Counts executed commands and keeps their text, so the round-trip budget is asserted rather than
    /// assumed and "no property table is queried" is observed on the wire (issue #214 AC3).
    /// </summary>
    private sealed class CommandCounter : IDbCommandInterceptor
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> texts = new();

        public int Count => texts.Count;

        public IReadOnlyCollection<string> Texts => texts.ToArray();

        public void Reset() => texts.Clear();

        public ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            texts.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            texts.Enqueue(command.CommandText);
            return result;
        }

        public ValueTask<object?> ScalarExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, object? result,
            CancellationToken cancellationToken = default)
        {
            texts.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
        {
            texts.Enqueue(command.CommandText);
            return result;
        }
    }

    private async Task<DbContextOptions<OdysseyContext>> MigratedSchemaAsync(CommandCounter? counter = null)
    {
        await DropAsync();

        await using (var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString)))
        {
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
        }

        var options = OptionsFor(fixture.ConnectionStringFor(Database), counter);
        await using (var context = new OdysseyContext(options))
        {
            await context.Database.MigrateAsync();
        }

        counter?.Reset();
        return options;
    }

    private async Task DropAsync()
    {
        await using var admin = new OdysseyContext(OptionsFor(fixture.OdysseyConnectionString));
        await admin.Database.ExecuteSqlRawAsync("DROP DATABASE IF EXISTS `" + Database + "`");
    }

    private static DbContextOptions<OdysseyContext> OptionsFor(string connectionString, CommandCounter? counter = null)
    {
        var builder = new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));

        if (counter is not null)
        {
            builder.AddInterceptors(counter);
        }

        return builder.Options;
    }
}
