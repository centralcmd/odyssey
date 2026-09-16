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
/// Real-engine coverage for the net-worth history (issue #90 AC15, AC16, AC21, AC26, AC36 and §12).
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

    // ── AC15 — five round trips, whatever the point count ─────────────────────────────────────

    [SkippableTheory]
    [InlineData(NetWorthInterval.Monthly, 2)]
    [InlineData(NetWorthInterval.Monthly, NetWorthHistoryQuery.MaxPoints)]
    [InlineData(NetWorthInterval.Daily, NetWorthHistoryQuery.MaxDailyPoints)]
    [InlineData(NetWorthInterval.Weekly, NetWorthHistoryQuery.MaxWeeklyPoints)]
    [InlineData(NetWorthInterval.Quarterly, NetWorthHistoryQuery.MaxPoints)]
    [InlineData(NetWorthInterval.Yearly, NetWorthHistoryQuery.MaxPoints)]
    public async Task TheComputation_CostsFiveRoundTrips_RegardlessOfPointCount(
        NetWorthInterval interval, int points)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var counter = new CommandCounter();
        var options = await MigratedSchemaAsync(counter);

        await using var context = new OdysseyContext(options);
        await SeedPortfolioAsync(context);

        var from = NetWorthPeriods.AddSaturating(DateOnly.FromDateTime(FixedNow), interval, -(points - 1));

        counter.Reset();
        var history = await ServiceFor(context).ComputeAsync(new NetWorthHistoryQuery
        {
            MainCurrency = "USD",
            Interval = interval,
            From = from,
            To = DateOnly.FromDateTime(FixedNow),
        });

        Assert.NotEmpty(history.Points);

        // The currency check, the accounts, the bucketed aggregate, the estimates and the rate
        // timeline. A per-point query would make a 120-point request 120 times this.
        Assert.Equal(5, counter.Count);
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
        });

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
        });

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
        await service.ComputeAsync(query);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        counter.Reset();
        var stopwatch = Stopwatch.StartNew();
        var history = await service.ComputeAsync(query);
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

    private static Account NewAccount(Guid id, string name, AccountType type, string currency, DateTime opened) => new()
    {
        AccountId = id,
        Name = name,
        Description = name,
        Opened = opened,
        AccountType = type,
        CurrencyCode = currency,
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

    /// <summary>Counts executed commands, so the round-trip budget is asserted rather than assumed.</summary>
    private sealed class CommandCounter : IDbCommandInterceptor
    {
        private int count;

        public int Count => count;

        public void Reset() => Interlocked.Exchange(ref count, 0);

        public ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return ValueTask.FromResult(result);
        }

        public DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            Interlocked.Increment(ref count);
            return result;
        }

        public ValueTask<object?> ScalarExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, object? result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return ValueTask.FromResult(result);
        }

        public object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
        {
            Interlocked.Increment(ref count);
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
