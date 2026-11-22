// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid, a table name or a
// constant this test generated itself — there is no external input — and the rows are deliberately written outside
// the services, in shapes the services no longer allow.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// <c>UniqueJournalAndTaskTagNames</c> on real MariaDB. A database from before the rule can hold several tags whose
/// names the new unique index treats as one; the migration renames all but one per group, then creates the index.
/// These are shapes the demo seed never produces, so this is where the rename is proved.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class UniqueJournalAndTaskTagNamesMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_unique_tag_names";
    private const string Migration = "_UniqueJournalAndTaskTagNames";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_RemoveLegacyFileBlobStorage";

    [SkippableTheory]
    [InlineData("JournalTags", "JournalTagId")]
    [InlineData("JournalTaskTags", "JournalTaskTagId")]
    public async Task Colliding_names_are_made_distinct_and_the_index_then_refuses_a_duplicate(string table, string idColumn)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await MigrationSeam.MigrateToAsync(context, Baseline);

            // An active tag and two archived ones with its name: the active one keeps it, and the archived ones are
            // numbered newest first.
            var bills = await InsertAsync(context, table, idColumn, "Bills", archived: null);
            var billsNewer = await InsertAsync(context, table, idColumn, "bills", archived: "2025-06-01");
            var billsOlder = await InsertAsync(context, table, idColumn, "BILLS", archived: "2024-01-01");

            // Two archived tags only: the most recently archived keeps the name. The other's first suffix collides
            // with a tag already called "Garden (2)", which keeps ITS name, so the renamed one is suffixed again.
            var gardenKept = await InsertAsync(context, table, idColumn, "Garden", archived: "2025-03-01");
            var gardenOther = await InsertAsync(context, table, idColumn, "garden", archived: "2023-03-01");
            var gardenTwo = await InsertAsync(context, table, idColumn, "Garden (2)", archived: null);

            // A full-length name keeps the column's 64 characters once suffixed.
            var longName = new string('x', 64);
            await InsertAsync(context, table, idColumn, longName, archived: null);
            var longOther = await InsertAsync(context, table, idColumn, longName.ToUpperInvariant(), archived: "2025-01-01");

            await MigrationRunner.MigrateAsync(context, CancellationToken.None);

            Assert.True(await MigrationSeam.HasRunAsync(context, Migration));
            Assert.Equal(0L, await MigrationSeam.CountAsync(context,
                $"SELECT COUNT(*) FROM (SELECT `Name` FROM `{table}` GROUP BY `Name` HAVING COUNT(*) > 1) d"));

            Assert.Equal("Bills", await NameOfAsync(context, table, idColumn, bills));
            Assert.Equal("bills (2)", await NameOfAsync(context, table, idColumn, billsNewer));
            Assert.Equal("BILLS (3)", await NameOfAsync(context, table, idColumn, billsOlder));

            Assert.Equal("Garden", await NameOfAsync(context, table, idColumn, gardenKept));
            Assert.Equal("Garden (2)", await NameOfAsync(context, table, idColumn, gardenTwo));
            var renamedGarden = await NameOfAsync(context, table, idColumn, gardenOther);
            Assert.StartsWith("garden (2) (", renamedGarden, StringComparison.Ordinal);

            var renamedLong = await NameOfAsync(context, table, idColumn, longOther);
            Assert.Equal(64, renamedLong.Length);
            Assert.EndsWith(" (2)", renamedLong, StringComparison.Ordinal);

            // Archive state is untouched, and the procedure is gone.
            Assert.Equal(2L, await MigrationSeam.CountAsync(context,
                $"SELECT COUNT(*) FROM `{table}` WHERE `Archived` IS NULL AND `Name` IN ('Bills', 'Garden (2)')"));
            Assert.Equal(0L, await MigrationSeam.CountAsync(context,
                $"SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '{Database}'"));

            // The index now refuses a case-folded duplicate on its own, under the name the services' duplicate-key
            // guard matches (DbErrors.IsDuplicateKeyOn reads it from the error message).
            Assert.Equal(1L, await MigrationSeam.CountAsync(context,
                "SELECT COUNT(DISTINCT INDEX_NAME) FROM information_schema.STATISTICS "
                + $"WHERE TABLE_SCHEMA = '{Database}' AND TABLE_NAME = '{table}' AND INDEX_NAME = 'IX_{table}_Name' AND NON_UNIQUE = 0"));
            var duplicate = await Assert.ThrowsAsync<MySqlException>(() => InsertAsync(context, table, idColumn, "BILLS", archived: null));
            Assert.Equal(1062, duplicate.Number);
            Assert.Contains($"IX_{table}_Name'", duplicate.Message, StringComparison.Ordinal);
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>A table without duplicates passes through unchanged.</summary>
    [SkippableFact]
    public async Task A_database_without_duplicates_upgrades_without_renaming_anything()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await MigrationSeam.MigrateToAsync(context, Baseline);
            var travel = await InsertAsync(context, "JournalTags", "JournalTagId", "Travel", archived: null);
            var urgent = await InsertAsync(context, "JournalTaskTags", "JournalTaskTagId", "Urgent", archived: "2025-01-01");

            await MigrationRunner.MigrateAsync(context, CancellationToken.None);

            Assert.True(await MigrationSeam.HasRunAsync(context, Migration));
            Assert.Equal("Travel", await NameOfAsync(context, "JournalTags", "JournalTagId", travel));
            Assert.Equal("Urgent", await NameOfAsync(context, "JournalTaskTags", "JournalTaskTagId", urgent));
        }
        finally
        {
            await DropAsync();
        }
    }

    private static async Task<Guid> InsertAsync(OdysseyContext context, string table, string idColumn, string name, string? archived)
    {
        var id = Guid.NewGuid();
        var archivedSql = archived is null ? "NULL" : $"'{archived} 00:00:00'";
        await context.Database.ExecuteSqlRawAsync(
            $"INSERT INTO `{table}` (`{idColumn}`, `Name`, `Archived`) VALUES ('{id}', {{0}}, {archivedSql})", name);
        return id;
    }

    private static async Task<string> NameOfAsync(OdysseyContext context, string table, string idColumn, Guid id) =>
        (string)(await MigrationSeam.ScalarAsync(context, $"SELECT `Name` FROM `{table}` WHERE `{idColumn}` = '{id}'"))!;

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

    private async Task RecreateAsync()
    {
        await DropAsync();
        await using var server = fixture.CreateOdysseyContext();
        await server.Database.ExecuteSqlRawAsync($"CREATE DATABASE `{Database}`");
    }

    private async Task DropAsync()
    {
        await using var server = fixture.CreateOdysseyContext();
        await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
    }
}
