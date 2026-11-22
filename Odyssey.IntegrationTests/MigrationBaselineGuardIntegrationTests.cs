#pragma warning disable EF1001 // MigrationsAssembly is EF's own implementation; subclassing it keeps the real snapshot.
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Context.Migrations;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Real-engine coverage for the cut-point guard and the same-id rule (issue #581 AC 2–9).
/// </summary>
[Collection(MariaDbCollection.Name)]
public class MigrationBaselineGuardIntegrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_baseline_guard";

    /// <summary>AC 2 and AC 3: a fresh database migrates, and re-running at head is a no-op.</summary>
    [SkippableFact]
    public async Task A_fresh_database_migrates_and_a_rerun_at_head_has_nothing_to_do()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateEmptyAsync();
        try
        {
            await using (var context = Context())
            {
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
            }

            var before = await HistoryAsync();
            await using (var context = Context())
            {
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
                await MigrationRunner.MigrateAsync(context, CancellationToken.None);
            }

            Assert.Equal(before, await HistoryAsync());
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// AC 4 — the same-id rule itself. A database at head under the full chain, opened through an assembly holding
    /// ONE migration carrying the head id (what a rebaseline ships), sees nothing pending, and the job changes
    /// nothing. This is the premise the whole procedure rests on: EF ignores applied ids its assembly lacks.
    /// </summary>
    [SkippableFact]
    public async Task A_database_at_the_cut_sees_a_single_baseline_with_the_head_id_as_applied()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        try
        {
            var before = await HistoryAsync();
            var tablesBefore = await TableCountAsync();

            await using (var context = SquashedContext())
            {
                var shipped = context.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
                Assert.Equal([before.Max(StringComparer.Ordinal)!], shipped);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());

                await MigrationRunner.MigrateAsync(context, new MigrationBaseline(shipped[0], "v0.0.1"), CancellationToken.None);
            }

            Assert.Equal(before, await HistoryAsync());
            Assert.Equal(tablesBefore, await TableCountAsync());
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>AC 5 — the converse: one id short of the cut, the squashed build refuses before touching anything.</summary>
    [SkippableFact]
    public async Task A_database_one_migration_short_of_the_cut_is_refused_by_the_squashed_build()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        try
        {
            var head = (await HistoryAsync()).Max(StringComparer.Ordinal)!;
            // A database of the old chain stopped one migration short holds that chain's earlier rows, not the cut's.
            // Since the rebaseline the chain is one migration long, so the earlier row is a stand-in.
            await ExecuteAsync($"DELETE FROM `__EFMigrationsHistory` WHERE MigrationId = '{head}'; " +
                "INSERT INTO `__EFMigrationsHistory` (MigrationId, ProductVersion) VALUES ('20200101000000_OneBeforeTheCut', '9.0.0')");
            var before = await HistoryAsync();

            await using (var context = SquashedContext())
            {
                var error = await Assert.ThrowsAsync<MigrationBaselineException>(() =>
                    MigrationRunner.MigrateAsync(context, new MigrationBaseline(head, "v0.0.1"), CancellationToken.None));

                Assert.Equal(MigrationBaselineGap.BelowBaseline, error.Gap);
                Assert.Contains("Upgrade to v0.0.1 first", error.Message);
            }

            Assert.Equal(before, await HistoryAsync());
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>AC 6 — older than the baseline: "recreate" with no bridge, the bridge release with one; nothing written.</summary>
    [SkippableTheory]
    [InlineData(null)]
    [InlineData("v9.9.9")]
    public async Task A_history_older_than_the_baseline_is_refused_with_its_remedy(string? bridge)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateWithHistoryAsync("20200101000000_Ancient");
        try
        {
            await using var context = Context();
            var baseline = MigrationBaseline.Current with { BridgeRelease = bridge };

            var error = await Assert.ThrowsAsync<MigrationBaselineException>(() =>
                MigrationRunner.MigrateAsync(context, baseline, CancellationToken.None));

            Assert.Equal(MigrationBaselineGap.BelowBaseline, error.Gap);
            Assert.Equal("20200101000000_Ancient", error.NewestAppliedId);
            Assert.Contains(MigrationBaseline.Current.BaselineMigrationId, error.Message);
            Assert.Contains(bridge is null ? "must be recreated" : $"Upgrade to {bridge} first", error.Message);
            Assert.Equal(["20200101000000_Ancient"], await HistoryAsync());
            Assert.Equal(1, await TableCountAsync());
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>AC 7 — a history whose newest id is later than the baseline but which lacks it is a foreign set.</summary>
    [SkippableFact]
    public async Task A_history_lacking_the_baseline_but_newer_than_it_is_a_foreign_set()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateWithHistoryAsync("20990101000000_SomethingElse");
        try
        {
            await using var context = Context();

            var error = await Assert.ThrowsAsync<MigrationBaselineException>(() =>
                MigrationRunner.MigrateAsync(context, CancellationToken.None));

            Assert.Equal(MigrationBaselineGap.ForeignSet, error.Gap);
            Assert.Contains("different migration set", error.Message);
            Assert.Equal(1, await TableCountAsync());
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>AC 8 — no history table over an empty schema is a fresh install.</summary>
    [SkippableFact]
    public async Task A_missing_history_table_is_a_fresh_install()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateEmptyAsync();
        try
        {
            await using var context = Context();

            await MigrationRunner.GuardAgainstBaselineGapAsync(context, MigrationBaseline.Current, CancellationToken.None);

            Assert.Equal(0, await TableCountAsync());
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>AC 9 — below the baseline AND drifted reports the baseline gap, because that guard runs first.</summary>
    [SkippableFact]
    public async Task A_database_below_the_baseline_and_drifted_reports_the_baseline_gap()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);
        try
        {
            await ExecuteAsync("DELETE FROM `__EFMigrationsHistory`; " +
                "INSERT INTO `__EFMigrationsHistory` (MigrationId, ProductVersion) VALUES ('20200101000000_Ancient', '9.0.0')");

            await using var context = Context();

            var error = await Assert.ThrowsAsync<MigrationBaselineException>(() =>
                MigrationRunner.MigrateAsync(context, CancellationToken.None));
            Assert.Equal(MigrationBaselineGap.BelowBaseline, error.Gap);
        }
        finally
        {
            await DropAsync();
        }
    }

    private OdysseyContext Context() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), new MariaDbServerVersion(new Version(11, 4, 0)))
            .Options);

    /// <summary>The context as a rebaselined build would see the database: one migration, carrying the head id.</summary>
    private OdysseyContext SquashedContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), new MariaDbServerVersion(new Version(11, 4, 0)))
            .ReplaceService<IMigrationsAssembly, SquashedMigrationsAssembly>()
            .Options);

    private async Task RecreateEmptyAsync() =>
        await ServerExecuteAsync($"DROP DATABASE IF EXISTS `{Database}`; CREATE DATABASE `{Database}`;");

    private async Task RecreateWithHistoryAsync(string migrationId)
    {
        await RecreateEmptyAsync();
        await ExecuteAsync(
            "CREATE TABLE `__EFMigrationsHistory` (`MigrationId` varchar(150) NOT NULL PRIMARY KEY, `ProductVersion` varchar(32) NOT NULL); " +
            $"INSERT INTO `__EFMigrationsHistory` VALUES ('{migrationId}', '9.0.0')");
    }

    private Task DropAsync() => ServerExecuteAsync($"DROP DATABASE IF EXISTS `{Database}`");

    private async Task ServerExecuteAsync(string sql)
    {
        await using var server = new MySqlConnection(fixture.OdysseyConnectionString);
        await server.OpenAsync();
        await using var command = new MySqlCommand(sql, server);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new MySqlConnection(fixture.ConnectionStringFor(Database));
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> HistoryAsync()
    {
        await using var connection = new MySqlConnection(fixture.ConnectionStringFor(Database));
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SELECT MigrationId FROM `__EFMigrationsHistory` ORDER BY MigrationId", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<string>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private async Task<long> TableCountAsync()
    {
        await using var connection = new MySqlConnection(fixture.ConnectionStringFor(Database));
        await connection.OpenAsync();
        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}

/// <summary>
/// What a rebaselined build ships, simulated: the real assembly's snapshot, and a single migration carrying the
/// real chain's head id. Its <c>Up</c> is empty — it never runs against a database at the cut, which is the point.
/// </summary>
internal sealed class SquashedMigrationsAssembly(
    ICurrentDbContext currentContext,
    IDbContextOptions options,
    IMigrationsIdGenerator idGenerator,
    IDiagnosticsLogger<DbLoggerCategory.Migrations> logger)
    : MigrationsAssembly(currentContext, options, idGenerator, logger)
{
    private IReadOnlyDictionary<string, TypeInfo>? squashed;

    public override IReadOnlyDictionary<string, TypeInfo> Migrations =>
        squashed ??= new Dictionary<string, TypeInfo>
        {
            [base.Migrations.Keys.Max(StringComparer.Ordinal)!] = typeof(SquashedBaseline).GetTypeInfo(),
        };

    internal sealed class SquashedBaseline : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }
    }
}
