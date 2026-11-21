// EF1002 flags interpolation into ExecuteSqlRawAsync. The only value interpolated below is the
// database name this test declares itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #140 (AC 17): a written <c>Paused</c> stamp round-trips to the same UTC
/// value with no precision loss, and the column carries no index.
/// </summary>
/// <remarks>
/// Not observable on the fast tiers: the EF InMemory provider stores a <see cref="DateTime"/> as the CLR
/// value it was given, so a round-trip through it can never lose the sub-second precision a real
/// <c>datetime(6)</c> column truncates at.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContractPauseMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_pause";

    /// <summary>
    /// The stamp round-trips to the same UTC value. <c>datetime(6)</c> keeps microseconds, so a value
    /// carrying them is the one that would expose a column scaffolded at a coarser precision.
    /// </summary>
    [SkippableFact]
    public async Task A_written_stamp_round_trips_with_no_precision_loss()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);

        var contract = Guid.NewGuid();
        var paused = new DateTime(2026, 9, 19, 13, 47, 5, DateTimeKind.Utc).AddTicks(1234560);

        try
        {
            await using (var context = NewContext())
            {
                context.Contracts.Add(new Contract
                {
                    ContractId = contract,
                    Name = "Frozen membership",
                    Type = ContractType.Membership,
                    StartDate = DateTime.UtcNow.AddYears(-1),
                    Paused = paused,
                    CreatedAtUtc = DateTime.UtcNow,
                });
                await context.SaveChangesAsync();
            }

            // A fresh context, so the value comes off the wire rather than out of the change tracker.
            await using (var context = NewContext())
            {
                var row = await context.Contracts.AsNoTracking().SingleAsync(c => c.ContractId == contract);

                Assert.Equal(paused, row.Paused);

                // Clearing it is a write like any other, and it is the release valve the whole guard
                // design rests on — so the column has to accept NULL back after holding a value.
                row.Paused = null;
                context.Contracts.Attach(row).Property(c => c.Paused).IsModified = true;
                await context.SaveChangesAsync();
            }

            await using (var context = NewContext())
            {
                Assert.Null((await context.Contracts.AsNoTracking()
                    .SingleAsync(c => c.ContractId == contract)).Paused);

                // No index: the derived status is computed in memory after projection, so the column
                // is never a SQL predicate and an index would be pure write cost. Asserted because an
                // index added later without that reasoning is exactly what this records.
                Assert.False(await IndexExistsOnAsync(context, "Paused"));
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
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Contracts' AND COLUMN_NAME = '{column}'
            """) > 0;

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
