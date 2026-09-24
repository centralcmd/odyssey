// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// formatted DateTime this test generated itself — there is no external input — and the pre-migration
// rows are deliberately written outside the service, which is the whole point of the seam.
#pragma warning disable EF1002

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #159 (AC 25): <c>AddTermDirection</c> applies cleanly over
/// pre-existing <c>Terms</c> rows, and every one of them reads back <c>Outgoing</c> — which is what
/// makes the change behaviour-preserving without a single data statement.
/// </summary>
/// <remarks>
/// Not observable on the fast tiers. The EF InMemory provider runs no migrations and holds no schema,
/// so "the column was added with a default and the existing rows carry it" is unrunnable there by
/// construction — and the whole compatibility claim rests on the DATABASE default rather than on
/// anything application code does. The seam (<see cref="MigrationSeam"/>) is what lets the "before"
/// rows exist at all; an ordinary fixture migrates straight to head.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class TermDirectionMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_term_direction";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_RetireUnspecifiedAndServiceProviderPartyRoles";

    private const string Subject = "_AddTermDirection";

    /// <summary>
    /// A term written before the column existed reads back <c>Outgoing</c>. The migration carries no
    /// backfill statement because the column's <c>DEFAULT 0</c> is the backfill: every term that
    /// existed before this change was a cost, so <c>Outgoing</c> is what each already meant.
    /// </summary>
    [SkippableFact]
    public async Task Pre_existing_terms_read_back_as_outgoing()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var contractId = Guid.NewGuid();
        var accountTermId = Guid.NewGuid();
        var contractTermId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                Assert.False(await ColumnExistsAsync(context, "Direction"));

                // Both owners, because the column lives on the shared table and the compatibility
                // claim has to hold for an account term as much as a contract one.
                await SeedOwnersAsync(context, contractId, accountId);
                await SeedTermAsync(context, contractTermId, contractId: contractId, accountId: null);
                await SeedTermAsync(context, accountTermId, contractId: null, accountId: accountId);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                Assert.True(await MigrationSeam.HasRunAsync(context, Subject));
                Assert.True(await ColumnExistsAsync(context, "Direction"));

                var terms = await context.Terms.AsNoTracking()
                    .Where(t => t.TermId == contractTermId || t.TermId == accountTermId)
                    .ToListAsync();

                Assert.Equal(2, terms.Count);
                Assert.All(terms, t => Assert.Equal(TermDirection.Outgoing, t.Direction));

                // No index. Direction is never a SQL predicate — the roll-up's one query narrows by
                // contract, kind, unit and effective date, and the split happens in memory after the
                // series collapse — so an index on a two-value column would be pure write cost.
                // Asserted because an index added later without that reasoning is what this records.
                Assert.False(await IndexExistsOnAsync(context, "Direction"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The column is NOT NULL, so a written direction round-trips and nothing can store "unknown".
    /// Two values only: a non-movement term (a deposit held, a notional cap) is deliberately not a
    /// third member, so there is no null state for a reader to interpret.
    /// </summary>
    [SkippableFact]
    public async Task A_written_direction_round_trips_and_the_column_rejects_null()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var contractId = Guid.NewGuid();
        var termId = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();
                context.Contracts.Add(new Contract
                {
                    ContractId = contractId,
                    Name = "Employment Agreement",
                    Type = ContractType.Employment,
                    StartDate = DateTime.UtcNow.AddYears(-1),
                    CreatedAtUtc = DateTime.UtcNow,
                });
                context.Terms.Add(new Term
                {
                    TermId = termId,
                    ContractId = contractId,
                    Label = "Base salary",
                    LabelKey = "base salary",
                    ValueUnit = TermValueUnit.Amount,
                    Value = 600000m,
                    CurrencyCode = "USD",
                    Interval = Interval.Annually,
                    IntervalCount = 1,
                    Direction = TermDirection.Incoming,
                    EffectiveFrom = DateTime.UtcNow.AddMonths(-6),
                    CreatedAtUtc = DateTime.UtcNow,
                });
                await context.SaveChangesAsync();
            }

            // A fresh context, so the value comes off the wire rather than out of the change tracker.
            await using (var context = NewContext())
            {
                Assert.Equal(TermDirection.Incoming,
                    (await context.Terms.AsNoTracking().SingleAsync(t => t.TermId == termId)).Direction);

                Assert.Equal("NO", await NullabilityOfAsync(context, "Direction"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Schema introspection ───────────────────────────────────────────────────

    private static async Task<bool> ColumnExistsAsync(OdysseyContext context, string column) =>
        await MigrationSeam.CountAsync(context, $"""
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Terms' AND COLUMN_NAME = '{column}'
            """) > 0;

    private static async Task<bool> IndexExistsOnAsync(OdysseyContext context, string column) =>
        await MigrationSeam.CountAsync(context, $"""
            SELECT COUNT(*) FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Terms' AND COLUMN_NAME = '{column}'
            """) > 0;

    private static async Task<string> NullabilityOfAsync(OdysseyContext context, string column) =>
        await MigrationSeam.CountAsync(context, $"""
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Terms'
              AND COLUMN_NAME = '{column}' AND IS_NULLABLE = 'NO'
            """) > 0 ? "NO" : "YES";

    private static Task SeedOwnersAsync(OdysseyContext context, Guid contractId, Guid accountId)
    {
        var now = Timestamp();
        return context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `Contracts` (`ContractId`, `Name`, `Type`, `CreatedAtUtc`)
            VALUES ('{contractId}', 'Pre-existing agreement', 0, '{now}');
            INSERT INTO `Accounts` (`AccountId`, `Name`, `Description`, `AccountType`, `CurrencyCode`, `Opened`)
            VALUES ('{accountId}', 'Pre-existing account', 'Seeded before the column existed.', 1, 'USD', '{now}');
            """);
    }

    private static Task SeedTermAsync(OdysseyContext context, Guid termId, Guid? contractId, Guid? accountId)
    {
        var now = Timestamp();
        var contract = contractId is { } c ? $"'{c}'" : "NULL";
        var account = accountId is { } a ? $"'{a}'" : "NULL";
        return context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `Terms`
                (`TermId`, `AccountId`, `ContractId`, `TermKind`, `Label`, `LabelKey`, `ValueUnit`,
                 `Value`, `CurrencyCode`, `Interval`, `IntervalCount`, `EffectiveFrom`, `CreatedAtUtc`)
            VALUES
                ('{termId}', {account}, {contract}, 10, 'Monthly rent', 'monthly rent', 1,
                 1850.000000, 'USD', 3, 1, '{now}', '{now}');
            """);
    }

    private static string Timestamp() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

    // ── Fixture plumbing ───────────────────────────────────────────────────────

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync()
    {
        await DropAsync();
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropAsync()
    {
        await using var server = ServerContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }

    private OdysseyContext ServerContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}
