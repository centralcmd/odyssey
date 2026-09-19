// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// formatted DateTime this test generated itself — there is no external input — and the pre-migration
// row is deliberately written outside the service, which is the whole point of the seam.
#pragma warning disable EF1002

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #140 (AC 17): <c>AddContractPause</c> applies cleanly over
/// pre-existing contract rows, every one of which reads back <c>Paused == null</c>, and a written
/// stamp round-trips to the same UTC value with no precision loss.
/// </summary>
/// <remarks>
/// Neither half is observable on the fast tiers. The EF InMemory provider has no schema and no
/// migrations, so "the column was added and the existing rows are null" is unrunnable there by
/// construction; and it stores a <see cref="DateTime"/> as the CLR value it was given, so a
/// round-trip through it can never lose the sub-second precision a real <c>datetime(6)</c> column
/// truncates at. The seam (<see cref="MigrationSeam"/>) is what lets the "before" row exist at all —
/// an ordinary fixture migrates straight to head.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContractPauseMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_pause";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_AddContractSummaryWindows";

    private const string Subject = "_AddContractPause";

    /// <summary>
    /// A contract written before the column existed reads back not paused — <c>NULL</c> already says
    /// "not paused", which is why the migration carries no backfill.
    /// </summary>
    [SkippableFact]
    public async Task Pre_existing_rows_read_back_as_not_paused()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var contract = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                Assert.False(await ColumnExistsAsync(context, "Paused"));
                await SeedContractAsync(context, contract);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                Assert.True(await MigrationSeam.HasRunAsync(context, Subject));
                Assert.True(await ColumnExistsAsync(context, "Paused"));

                var row = await context.Contracts.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.ContractId == contract);
                Assert.NotNull(row);
                Assert.Null(row!.Paused);

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

    /// <summary>
    /// The stamp round-trips to the same UTC value. <c>datetime(6)</c> keeps microseconds, so a value
    /// carrying them is the one that would expose a column scaffolded at a coarser precision.
    /// </summary>
    [SkippableFact]
    public async Task A_written_stamp_round_trips_with_no_precision_loss()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var contract = Guid.NewGuid();
        var paused = new DateTime(2026, 9, 19, 13, 47, 5, DateTimeKind.Utc).AddTicks(1234560);

        try
        {
            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();
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
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Contracts' AND COLUMN_NAME = '{column}'
            """) > 0;

    private static async Task<bool> IndexExistsOnAsync(OdysseyContext context, string column) =>
        await MigrationSeam.CountAsync(context, $"""
            SELECT COUNT(*) FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Contracts' AND COLUMN_NAME = '{column}'
            """) > 0;

    private static Task SeedContractAsync(OdysseyContext context, Guid contractId)
    {
        var createdAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        return context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `Contracts` (`ContractId`, `Name`, `Type`, `CreatedAtUtc`)
            VALUES ('{contractId}', 'Pre-existing agreement', 3, '{createdAt}');
            """);
    }

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
