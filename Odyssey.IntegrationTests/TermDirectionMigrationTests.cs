// EF1002 flags interpolation into ExecuteSqlRawAsync. The only value interpolated below is the
// database name this test declares itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #159: <c>Terms.Direction</c> is <c>NOT NULL</c>, a written direction
/// round-trips, and the column carries no index.
/// </summary>
/// <remarks>
/// Not observable on the fast tiers: the EF InMemory provider holds no schema, so neither the column's
/// nullability nor the absence of an index can be asserted there.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class TermDirectionMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_term_direction";

    /// <summary>
    /// The column is NOT NULL, so a written direction round-trips and nothing can store "unknown".
    /// Two values only: a non-movement term (a deposit held, a notional cap) is deliberately not a
    /// third member, so there is no null state for a reader to interpret.
    /// </summary>
    [SkippableFact]
    public async Task A_written_direction_round_trips_and_the_column_rejects_null()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);

        var contractId = Guid.NewGuid();
        var termId = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
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

    // ── Schema introspection ───────────────────────────────────────────────────

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

    // ── Fixture plumbing ───────────────────────────────────────────────────────

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task DropAsync()
    {
        await using var server = fixture.CreateOdysseyContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }
}
