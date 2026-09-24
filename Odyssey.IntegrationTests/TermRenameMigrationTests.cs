// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a
// literal this test wrote itself — there is no external input — and the "before" rows have to be
// written against the PRE-rename column names the entity model no longer carries, which is what
// makes raw SQL the only way to build that state.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The migration behind the <c>AccountTerms</c> → <c>Terms</c> rename and the interval rework
/// (issue #120): the table, column and index renames, the two added columns, the foreign-key
/// re-identification, and the two data statements that remap the retired <c>Quarterly</c> ordinal
/// and backfill the identity count.
/// </summary>
/// <remarks>
/// <para>
/// Only observable here. The EF InMemory provider runs no migrations, holds no schema and enforces
/// no foreign keys, so every criterion in this class is unrunnable on the fast tiers by
/// construction — which is exactly the tier that would have reported the scaffolded
/// <c>DropTable</c> + <c>CreateTable</c> pair as working while it destroyed every stored row.
/// </para>
/// <para>
/// Migrations are named by their suffix rather than their timestamp: a re-scaffold changes the
/// timestamp and nothing else, and a test hardcoding it would fail for a reason unrelated to what it
/// asserts.
/// </para>
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class TermRenameMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_term_rename";

    /// <summary>The migration immediately before the rename — the last point at which the table is
    /// still <c>AccountTerms</c> and its cadence column still <c>BillingPeriod</c>.</summary>
    private const string Baseline = "_AddUserProfileImage";

    private const string Rename = "_RenameAccountTermsToTermsAndReworkInterval";

    private const string NewForeignKey = "FK_Terms_Accounts_AccountId";
    private const string OldForeignKey = "FK_AccountTerms_Accounts_AccountId";
    private const string NewIndex = "IX_Terms_AccountId_TermKind_LabelKey_EffectiveFrom";
    private const string OldIndex = "IX_AccountTerms_AccountId_TermKind_LabelKey_EffectiveFrom";

    /// <summary>
    /// AC 25, 26, 28-31, 33 — the renames preserve every stored value, and the two data statements
    /// land where the spec says.
    /// </summary>
    /// <remarks>
    /// AC 26 is the load-bearing one: EF scaffolded this migration as a drop-and-recreate, which
    /// would have emptied the primary key and the very column the remap reads. A row written as
    /// <c>AccountTermId = X, BillingPeriod = 2</c> must read back as <c>TermId = X, Interval = 2</c>.
    /// </remarks>
    [SkippableFact]
    public async Task The_rename_preserves_every_row_and_remaps_the_retired_ordinal()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var accountId = Guid.NewGuid();
        var quarterly = Guid.NewGuid();
        var perTransaction = Guid.NewGuid();
        var monthly = Guid.NewGuid();
        var daily = Guid.NewGuid();
        var annually = Guid.NewGuid();
        var noInterval = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await InsertAccountAsync(context, accountId);

                await InsertTermAsync(context, quarterly, accountId, billingPeriod: 4, label: "Quarterly charge");
                await InsertTermAsync(context, perTransaction, accountId, billingPeriod: 1, label: "Per occurrence");
                await InsertTermAsync(context, monthly, accountId, billingPeriod: 3, label: "Paper statement");
                await InsertTermAsync(context, daily, accountId, billingPeriod: 2, label: "Daily charge");
                await InsertTermAsync(context, annually, accountId, billingPeriod: 5, label: "Annual card fee");
                await InsertTermAsync(context, noInterval, accountId, billingPeriod: null, label: "Unset");
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Rename);

                Assert.True(await TableExistsAsync(context, "Terms"));
                Assert.False(await TableExistsAsync(context, "AccountTerms"));

                // Projected to the columns that existed AT THIS MIGRATION, not materialised as the
                // entity. The seam pins the SCHEMA to a point in history while the MODEL stays at
                // head, so a column added by a later migration — ContractId, since issue #135 —
                // would be selected by an entity read and not exist in the table. Anything the
                // assertions below do not name is deliberately absent from this projection.
                var terms = await context.Terms.AsNoTracking()
                    .Select(t => new
                    {
                        t.TermId,
                        t.Label,
                        t.Interval,
                        t.IntervalCount,
                        t.AnchorDate,
                    })
                    .ToListAsync();

                // AC 25 — the row count is identical across the rename.
                Assert.Equal(6, terms.Count);

                // AC 26 — the primary key travelled with its values, not as a fresh empty column. The
                // owner is read by raw SQL: the model at head has no Terms.AccountId (issue #190).
                Assert.Equal(6, await context.Database
                    .SqlQueryRaw<int>($"SELECT COUNT(*) AS `Value` FROM `Terms` WHERE `AccountId` = '{accountId}'")
                    .SingleAsync());
                Assert.All(terms, term => Assert.NotEqual(Guid.Empty, term.TermId));
                Assert.Equal("Daily charge", terms.Single(t => t.TermId == daily).Label);

                // AC 28 — Quarterly (4) becomes Monthly x 3.
                var remapped = terms.Single(t => t.TermId == quarterly);
                Assert.Equal(Interval.Monthly, remapped.Interval);
                Assert.Equal(3, remapped.IntervalCount);

                // AC 29 — a non-periodic unit keeps its ordinal and gets NO count.
                var occurrence = terms.Single(t => t.TermId == perTransaction);
                Assert.Equal(Interval.PerOccurrence, occurrence.Interval);
                Assert.Null(occurrence.IntervalCount);

                // AC 30 — every unit that was already periodic gets the identity count.
                Assert.Equal(1, terms.Single(t => t.TermId == monthly).IntervalCount);
                Assert.Equal(1, terms.Single(t => t.TermId == daily).IntervalCount);
                Assert.Equal(1, terms.Single(t => t.TermId == annually).IntervalCount);

                // A row with no interval at all is untouched by both statements.
                var unset = terms.Single(t => t.TermId == noInterval);
                Assert.Null(unset.Interval);
                Assert.Null(unset.IntervalCount);

                // AC 31 — no backfill: nothing invents an anchor, and nothing copies EffectiveFrom.
                Assert.All(terms, term => Assert.Null(term.AnchorDate));

                // AC 33 — the anchor matches EffectiveFrom's column type, to microsecond precision.
                Assert.Equal("datetime(6)", await ColumnTypeAsync(context, "Terms", "AnchorDate"));
                Assert.Equal(
                    await ColumnTypeAsync(context, "Terms", "EffectiveFrom"),
                    await ColumnTypeAsync(context, "Terms", "AnchorDate"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 30's second half — a pre-existing periodic row round-trips through a <c>PUT</c> rather than
    /// tripping the "count only for a periodic interval" rule on its first read-modify-write.
    /// </summary>
    [SkippableFact]
    public async Task A_backfilled_row_round_trips_through_the_service_after_the_migration()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var accountId = Guid.NewGuid();
        var termId = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await InsertAccountAsync(context, accountId);
                await InsertTermAsync(context, termId, accountId, billingPeriod: 3, label: "Paper statement");
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Rename);

                // …then on to head. This test exercises the real service, which reads through the
                // model at head and therefore selects every column the model has — including ones
                // added after the rename (ContractId, issue #135). Stopping at the rename would make
                // it fail on a missing column rather than on the backfill it is asserting. Completing
                // the run is also the truthful shape: a deployed application is always at head, and
                // the backfill written by the rename is what it reads. Since issue #190 the run to head
                // also moves the term onto a contract created for the account.
                await context.Database.MigrateAsync();

                var contractId = (await context.Terms.AsNoTracking().SingleAsync(t => t.TermId == termId)).ContractId;
                var service = new Odyssey.Core.Finance.TermService(context);
                var history = await service.GetContractHistory(contractId);
                var loaded = Assert.Single(history!);

                Assert.Equal(1, loaded.IntervalCount);

                // The read-modify-write a UI performs: send back exactly what was read.
                var updated = await service.UpdateForContract(contractId, termId, userId: null, putTerm: new Odyssey.Dtos.Finance.NewTerm
                {
                    Label = loaded.Label,
                    ValueUnit = loaded.ValueUnit,
                    Value = loaded.Value,
                    CurrencyCode = loaded.CurrencyCode,
                    Interval = loaded.Interval,
                    IntervalCount = loaded.IntervalCount,
                    AnchorDate = loaded.AnchorDate,
                    EffectiveFrom = loaded.EffectiveFrom,
                    Note = loaded.Note,
                });

                Assert.True(updated);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 27 — the foreign key and index are re-identified in both directions, and an
    /// <c>AccountId</c>-leading index exists at every intermediate step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That last property is errno 1553: InnoDB requires an FK column to have a backing index at all
    /// times, and this repository has already been bitten by it once, in
    /// <c>AddAccountTermLabel</c>'s scaffolded <c>DropIndex</c>-before-<c>CreateIndex</c> ordering.
    /// </para>
    /// <para>
    /// The primary key is asserted by its key COLUMNS and the <c>PRIMARY</c> constraint name, never
    /// by <c>PK_Terms</c>: MySQL and MariaDB assign no user-visible primary-key name, so an
    /// assertion on that string would be unverifiable rather than merely strict.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task The_foreign_key_and_index_are_renamed_and_revert_with_an_AccountId_index_throughout()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);

                Assert.True(await ForeignKeyExistsAsync(context, "AccountTerms", OldForeignKey));
                Assert.True(await LeadingAccountIdIndexExistsAsync(context, "AccountTerms"));
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Rename);

                Assert.True(await ForeignKeyExistsAsync(context, "Terms", NewForeignKey));
                Assert.False(await ForeignKeyExistsAsync(context, "Terms", OldForeignKey));

                Assert.True(await IndexExistsAsync(context, "Terms", NewIndex));
                Assert.False(await IndexExistsAsync(context, "Terms", OldIndex));

                Assert.True(await LeadingAccountIdIndexExistsAsync(context, "Terms"));
                Assert.Equal(["TermId"], await PrimaryKeyColumnsAsync(context, "Terms"));
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);

                Assert.True(await ForeignKeyExistsAsync(context, "AccountTerms", OldForeignKey));
                Assert.True(await IndexExistsAsync(context, "AccountTerms", OldIndex));
                Assert.False(await IndexExistsAsync(context, "AccountTerms", NewIndex));
                Assert.True(await LeadingAccountIdIndexExistsAsync(context, "AccountTerms"));
                Assert.Equal(["AccountTermId"], await PrimaryKeyColumnsAsync(context, "AccountTerms"));
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>AC 34 — the FK's <c>ON DELETE CASCADE</c> survived the drop and re-add.</summary>
    [SkippableFact]
    public async Task Deleting_an_account_still_cascades_to_its_terms_after_the_rename()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var accountId = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await InsertAccountAsync(context, accountId);
                await InsertTermAsync(context, Guid.NewGuid(), accountId, billingPeriod: 3, label: "Paper statement");
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Rename);
                Assert.Equal(1, await context.Terms.CountAsync());

                await context.Database.ExecuteSqlRawAsync(
                    $"DELETE FROM `Accounts` WHERE `AccountId` = '{accountId}'");

                Assert.Equal(0, await context.Terms.CountAsync());
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 32 — the migration is idempotent in the sense that matters: nothing satisfies
    /// <c>Interval = 4</c> afterwards, and re-running the whole sequence after a revert changes
    /// nothing further. The remap's predicate is its own complement, which is what buys that.
    /// </summary>
    [SkippableFact]
    public async Task No_row_holds_the_retired_ordinal_after_the_migration_has_run_twice()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var accountId = Guid.NewGuid();
        var quarterly = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await InsertAccountAsync(context, accountId);
                await InsertTermAsync(context, quarterly, accountId, billingPeriod: 4, label: "Quarterly charge");
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Rename);
                await MigrationSeam.MigrateToAsync(context, Baseline);
                await MigrationSeam.MigrateToAsync(context, Rename);

                Assert.Equal(0, await CountAtRetiredOrdinalAsync(context));

                // Down does NOT restore Quarterly — inventing a back-conversion would fabricate data
                // on rows a user may have authored as Monthly x 3 deliberately. The row therefore
                // stays Monthly, and the second Up gives it the identity count rather than 3.
                // Projected for the same reason as above: the schema is historical, the model is head.
                var term = await context.Terms.AsNoTracking()
                    .Select(t => new { t.Interval, t.IntervalCount })
                    .SingleAsync();
                Assert.Equal(Interval.Monthly, term.Interval);
                Assert.Equal(1, term.IntervalCount);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 46 — an interrupted rename is reported by name. Before the guard learned the
    /// <c>Rename*</c> operations, this half-state fell through <c>CreatedBy</c>'s default and the
    /// replay died on a bare <c>Table 'Terms' already exists</c> with no guidance.
    /// </summary>
    /// <remarks>
    /// The interruption is reproduced by its aftermath rather than by killing a process mid-run:
    /// migrate cleanly, then delete the history row. That is exactly the state MariaDB's implicit
    /// DDL commit leaves behind, and unlike a timing-based kill it is deterministic.
    /// </remarks>
    [SkippableFact]
    public async Task An_interrupted_rename_is_reported_as_drift_naming_the_renamed_table()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            string migrationId;

            await using (var context = NewContext())
            {
                migrationId = MigrationSeam.IdOf(context, Rename);
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
            }

            await using (var context = NewContext())
            {
                Assert.True(await TableExistsAsync(context, "Terms"));

                await context.Database.ExecuteSqlRawAsync(
                    "DELETE FROM `__EFMigrationsHistory` WHERE MigrationId = {0}", migrationId);
            }

            await using (var context = NewContext())
            {
                var error = await Assert.ThrowsAsync<MigrationDriftException>(
                    () => MigrationRunner.MigrateAsync(context, CancellationToken.None));

                Assert.Equal(migrationId, error.MigrationId);
                Assert.Contains("Terms", error.ExistingObject);
                Assert.Contains("docs/migration-history-drift.md", error.Message);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 47 — the guard stays NARROW. A clean database with every migration pending, and a fully
    /// migrated one with none, must both pass: the extension must not make an ordinary first run or
    /// an ordinary upgrade look like drift.
    /// </summary>
    [SkippableFact]
    public async Task A_clean_database_and_a_fully_migrated_one_both_pass_the_pre_flight_check()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using (var context = NewContext())
            {
                // Everything pending, nothing present — an ordinary first run.
                await MigrationRunner.GuardAgainstDriftAsync(context, CancellationToken.None);
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
            }

            await using (var context = NewContext())
            {
                // Nothing pending, everything present — an ordinary restart.
                await MigrationRunner.GuardAgainstDriftAsync(context, CancellationToken.None);
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 23 — the historical migration ids are untouched by the rename, and a full migrate records
    /// exactly the shipped set.
    /// </summary>
    /// <remarks>
    /// A migration id is a persisted string in <c>__EFMigrationsHistory</c>. Renaming the class
    /// renames the id, and every existing database would then see an unknown pending migration and
    /// replay it against a schema that already holds its objects — which is why
    /// <c>AddAccountTermLabel</c> and <c>CollapseFeeTermKinds</c> keep their names although the
    /// vocabulary around them moved on.
    /// </remarks>
    [SkippableFact]
    public async Task A_full_migrate_records_exactly_the_shipped_ids_including_the_historical_term_ones()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await MigrationRunner.MigrateAsync(context, CancellationToken.None);

            var shipped = context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
            var applied = (await context.Database.GetAppliedMigrationsAsync())
                .Order(StringComparer.Ordinal)
                .ToList();

            Assert.Equal(shipped, applied);

            Assert.Contains("20260911101930_AddAccountTermLabel", applied);
            Assert.Contains("20260911102006_CollapseFeeTermKinds", applied);
        }
        finally
        {
            await DropAsync();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Task InsertAccountAsync(OdysseyContext context, Guid accountId) =>
        context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `Accounts` (`AccountId`, `Name`, `Description`, `Opened`, `AccountType`, `CurrencyCode`) " +
            $"VALUES ('{accountId}', 'Travel card', 'liability', '2024-01-01 00:00:00', 4, 'USD')");

    /// <summary>
    /// A "before" row, written against the PRE-rename column names. Raw SQL is the only way: the
    /// entity model names <c>TermId</c> and <c>Interval</c>, which do not exist at the baseline.
    /// </summary>
    private static Task InsertTermAsync(
        OdysseyContext context, Guid termId, Guid accountId, int? billingPeriod, string label)
    {
        var period = billingPeriod?.ToString() ?? "NULL";

        return context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `AccountTerms` " +
            "(`AccountTermId`, `AccountId`, `TermKind`, `Label`, `LabelKey`, `ValueUnit`, `Value`, " +
            " `CurrencyCode`, `BillingPeriod`, `EffectiveFrom`, `Note`, `CreatedAtUtc`) " +
            $"VALUES ('{termId}', '{accountId}', 10, '{label}', '{label.ToLowerInvariant()}', 1, 25, " +
            $"'USD', {period}, '2024-01-01 00:00:00', NULL, '2024-01-01 00:00:00')");
    }

    private static async Task<int> CountAtRetiredOrdinalAsync(OdysseyContext context) =>
        await context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS `Value` FROM `Terms` WHERE `Interval` = 4")
            .SingleAsync();

    private static async Task<bool> TableExistsAsync(OdysseyContext context, string table) =>
        await context.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*) AS `Value` FROM information_schema.TABLES " +
                $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}'")
            .SingleAsync() > 0;

    private static async Task<string> ColumnTypeAsync(OdysseyContext context, string table, string column) =>
        await context.Database
            .SqlQueryRaw<string>(
                "SELECT COLUMN_TYPE AS `Value` FROM information_schema.COLUMNS " +
                $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'")
            .SingleAsync();

    private static async Task<bool> IndexExistsAsync(OdysseyContext context, string table, string name) =>
        await context.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*) AS `Value` FROM information_schema.STATISTICS " +
                $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND INDEX_NAME = '{name}'")
            .SingleAsync() > 0;

    /// <summary>Errno 1553's precondition, stated positively: SOME index leads with
    /// <c>AccountId</c>, whatever it happens to be called at this point in the sequence.</summary>
    private static async Task<bool> LeadingAccountIdIndexExistsAsync(OdysseyContext context, string table) =>
        await context.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*) AS `Value` FROM information_schema.STATISTICS " +
                $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' " +
                "AND SEQ_IN_INDEX = 1 AND COLUMN_NAME = 'AccountId'")
            .SingleAsync() > 0;

    private static async Task<List<string>> PrimaryKeyColumnsAsync(OdysseyContext context, string table) =>
        await context.Database
            .SqlQueryRaw<string>(
                "SELECT COLUMN_NAME AS `Value` FROM information_schema.KEY_COLUMN_USAGE " +
                $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' " +
                "AND CONSTRAINT_NAME = 'PRIMARY' ORDER BY ORDINAL_POSITION")
            .ToListAsync();

    private static async Task<bool> ForeignKeyExistsAsync(OdysseyContext context, string table, string name) =>
        await context.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*) AS `Value` FROM information_schema.TABLE_CONSTRAINTS " +
                $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' " +
                $"AND CONSTRAINT_TYPE = 'FOREIGN KEY' AND CONSTRAINT_NAME = '{name}'")
            .SingleAsync() > 0;

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
