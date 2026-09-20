// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// formatted DateTime this test generated itself — there is no external input — and the pre-migration
// rows are deliberately written outside the service, which is the whole point of the seam.
#pragma warning disable EF1002

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Finance;
using Xunit;
using ContextContractType = Odyssey.Context.ContractType;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The relational half of issue #145 (AC 10, AC 24): <c>AddContractSignatureDates</c> applies over
/// pre-existing contract rows, backfills both stamps per row from that row's own dates, and leaves
/// every contract in the status it already had.
/// </summary>
/// <remarks>
/// None of this is observable on the fast tiers. The EF InMemory provider has no schema and no
/// migrations, so a backfill written as raw SQL over three other columns is unrunnable there by
/// construction — and the one property the backfill has to get right, that a contract whose
/// <c>StartDate</c> is in the FUTURE still gets a past stamp, is a property of the SQL
/// <c>LEAST</c>/<c>COALESCE</c> expression rather than of any C# the fast tiers execute. The seam
/// (<see cref="MigrationSeam"/>) is what lets the "before" rows exist at all — an ordinary fixture
/// migrates straight to head.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ContractSignatureMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_contract_signature";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_AddContractEvents";

    private const string Subject = "_AddContractSignatureDates";

    /// <summary>
    /// AC 10 — no existing contract changes status. Every pre-migration row becomes signed, so the
    /// signature layer short-circuits nothing and the date chain runs exactly as before.
    ///
    /// <para>
    /// Leaving the columns null would NOT have been the safe alternative: the derivation reads a null
    /// <c>Signed</c> as unsigned, so an un-backfilled upgrade would have flipped every contract in the
    /// database to <c>Draft</c> and emptied the run rate and the upcoming charges overnight. That is
    /// what this asserts — the statuses across the seam, not the column values.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task No_existing_contract_changes_status_across_the_migration()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        // One row per pre-migration status, so a derivation that lost any branch is caught here and
        // not only on the one shape the fixture happened to pick.
        var active = Guid.NewGuid();
        var upcoming = Guid.NewGuid();
        var expired = Guid.NewGuid();
        var archived = Guid.NewGuid();
        var oneOff = Guid.NewGuid();

        var today = DateTime.UtcNow.Date;

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                Assert.False(await ColumnExistsAsync(context, "Ready"));
                Assert.False(await ColumnExistsAsync(context, "Signed"));

                await SeedAsync(context, active, start: today.AddDays(-30), end: today.AddDays(30));
                await SeedAsync(context, upcoming, start: today.AddDays(30), end: today.AddDays(400));
                await SeedAsync(context, expired, start: today.AddDays(-400), end: today.AddDays(-30));
                await SeedAsync(context, archived, start: today.AddDays(-400), end: today.AddDays(-30),
                    archived: today.AddDays(-10));
                await SeedAsync(context, oneOff, completion: today.AddDays(-5));
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                Assert.True(await MigrationSeam.HasRunAsync(context, Subject));
                Assert.True(await ColumnExistsAsync(context, "Ready"));
                Assert.True(await ColumnExistsAsync(context, "Signed"));

                // Neither column is indexed: the derived status is computed in memory after
                // projection, so neither is ever a SQL predicate and an index would be pure write
                // cost. Asserted because an index added later without that reasoning is exactly what
                // this records.
                Assert.False(await IndexExistsOnAsync(context, "Ready"));
                Assert.False(await IndexExistsOnAsync(context, "Signed"));

                var rows = await context.Contracts.AsNoTracking()
                    .ToDictionaryAsync(c => c.ContractId, c => c);

                // Every row is signed, and the four date-chain statuses read exactly as before.
                Assert.All(rows.Values, row => Assert.NotNull(row.Signed));
                Assert.Equal(ContractStatus.Active, Status(rows[active], today));
                Assert.Equal(ContractStatus.Upcoming, Status(rows[upcoming], today));
                Assert.Equal(ContractStatus.Expired, Status(rows[expired], today));
                Assert.Equal(ContractStatus.Archived, Status(rows[archived], today));
                // A settled one-off is Active, not Expired — a completed record, not a lapsed term.
                Assert.Equal(ContractStatus.Active, Status(rows[oneOff], today));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 24 — every backfilled row satisfies the guards the write path enforces: no row has
    /// <c>Signed &lt; Ready</c> (G2) and neither stamp is dated in the future (G3).
    ///
    /// <para>
    /// The <c>UPCOMING</c> row is the whole point. A bare
    /// <c>COALESCE(StartDate, CompletionDate, CreatedAtUtc)</c> would stamp a FUTURE <c>Signed</c>
    /// onto it, leaving a row no later <c>PUT</c> could save without first repairing it — so the
    /// <c>LEAST(…, CreatedAtUtc)</c> clamp is not redundant with the <c>COALESCE</c>, and the last
    /// assertion here is that a name-only edit of that row comes back clean rather than a <c>400</c>.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Backfilled_rows_satisfy_their_own_write_guards()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var upcoming = Guid.NewGuid();
        var backdated = Guid.NewGuid();
        var today = DateTime.UtcNow.Date;
        var createdAt = DateTime.UtcNow;

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await SeedAsync(context, upcoming, start: today.AddDays(90), end: today.AddDays(500),
                    createdAt: createdAt);
                // Backdated: its term began long before its row was created, so it keeps the earlier
                // StartDate rather than the clamp.
                await SeedAsync(context, backdated, start: today.AddYears(-5), end: today.AddYears(5),
                    createdAt: createdAt);
            }

            await using (var context = NewContext())
            {
                await context.Database.MigrateAsync();

                var rows = await context.Contracts.AsNoTracking()
                    .ToDictionaryAsync(c => c.ContractId, c => c);

                foreach (var row in rows.Values)
                {
                    Assert.NotNull(row.Ready);
                    Assert.NotNull(row.Signed);
                    // G2 and G3, read straight off the stored values.
                    Assert.True(row.Signed >= row.Ready, "Signed must not precede Ready.");
                    Assert.True(row.Signed!.Value.Date <= today, "Signed must not be in the future.");
                    Assert.True(row.Ready!.Value.Date <= today, "Ready must not be in the future.");
                }

                // The clamp: the Upcoming row takes CreatedAtUtc, not its future StartDate.
                Assert.True(rows[upcoming].Signed!.Value <= createdAt.AddSeconds(1));

                // The backdated row keeps its own earlier StartDate.
                Assert.Equal(today.AddYears(-5), rows[backdated].Signed!.Value.Date);
            }

            // Re-running the backfill leaves every value unchanged (AC 10): the WHERE clause is what
            // makes a migration interrupted after the column adds, and re-applied per
            // docs/migration-history-drift.md, safe to replay.
            await using (var context = NewContext())
            {
                var before = await context.Contracts.AsNoTracking()
                    .ToDictionaryAsync(c => c.ContractId, c => (c.Ready, c.Signed));

                await context.Database.ExecuteSqlRawAsync("""
                    UPDATE `Contracts`
                    SET `Ready`  = LEAST(COALESCE(`StartDate`, `CompletionDate`, `CreatedAtUtc`), `CreatedAtUtc`),
                        `Signed` = LEAST(COALESCE(`StartDate`, `CompletionDate`, `CreatedAtUtc`), `CreatedAtUtc`)
                    WHERE `Signed` IS NULL;
                    """);

                var after = await context.Contracts.AsNoTracking()
                    .ToDictionaryAsync(c => c.ContractId, c => (c.Ready, c.Signed));

                Assert.Equal(before, after);
            }

            // And a later ordinary write against a backfilled row is accepted, which is the failure
            // a guard-violating backfill would actually produce for a user.
            await using (var context = NewContext())
            {
                var row = await context.Contracts.SingleAsync(c => c.ContractId == upcoming);
                row.Name = "Renamed after the upgrade";
                await context.SaveChangesAsync();
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// The derivation, restated over stored rows. Kept as a local mirror rather than reaching into
    /// <c>ContractService</c>: this asserts what the DATABASE holds after the migration, and a shared
    /// helper would let a change to the derivation quietly redefine what "unchanged status" means.
    /// </summary>
    private static ContractStatus Status(Contract c, DateTime today)
    {
        if (c.Archived is not null)
        {
            return ContractStatus.Archived;
        }
        if (c.Signed is null)
        {
            return c.Ready is not null ? ContractStatus.Ready : ContractStatus.Draft;
        }
        if (c.CompletionDate is { } completion)
        {
            return completion.Date > today ? ContractStatus.Upcoming : ContractStatus.Active;
        }
        if (c.StartDate is { } start && start.Date > today)
        {
            return ContractStatus.Upcoming;
        }
        if (c.EndDate is { } end && end.Date < today)
        {
            return ContractStatus.Expired;
        }
        return c.Paused is not null ? ContractStatus.Paused : ContractStatus.Active;
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

    /// <summary>
    /// A contract written the way one existed BEFORE the columns did — raw SQL, outside the service,
    /// which is what the seam exists for.
    /// </summary>
    private static Task SeedAsync(
        OdysseyContext context, Guid contractId,
        DateTime? start = null, DateTime? end = null, DateTime? completion = null,
        DateTime? archived = null, DateTime? createdAt = null)
    {
        static string Sql(DateTime? value) => value is { } v
            ? $"'{v.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}'"
            : "NULL";

        return context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO `Contracts`
                (`ContractId`, `Name`, `Type`, `StartDate`, `EndDate`, `CompletionDate`, `Archived`, `CreatedAtUtc`)
            VALUES
                ('{contractId}', 'Pre-existing agreement', 3, {Sql(start)}, {Sql(end)}, {Sql(completion)},
                 {Sql(archived)}, {Sql(createdAt ?? DateTime.UtcNow)});
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
