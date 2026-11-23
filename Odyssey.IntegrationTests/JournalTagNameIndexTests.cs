// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid, a table name or a
// constant this test generated itself — there is no external input.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The unique, case-folding name index on journal and task tags, on real MariaDB. Its data migration
/// (<c>UniqueJournalAndTaskTagNames</c>) was retired with the November 2026 rebaseline; what stays is the schema it
/// left, which the services' duplicate-key guard relies on by index name.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class JournalTagNameIndexTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_journal_tag_name_index";

    [SkippableTheory]
    [InlineData("JournalTags", "JournalTagId")]
    [InlineData("JournalTaskTags", "JournalTaskTagId")]
    public async Task The_index_refuses_a_case_folded_duplicate_under_its_own_name(string table, string idColumn)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);

        try
        {
            await using var context = NewContext();
            await InsertAsync(context, table, idColumn, "Bills", archived: null);

            // Unique, under the name DbErrors.IsDuplicateKeyOn matches in the error message.
            Assert.Equal(1L, await MigrationSeam.CountAsync(context,
                "SELECT COUNT(DISTINCT INDEX_NAME) FROM information_schema.STATISTICS "
                + $"WHERE TABLE_SCHEMA = '{Database}' AND TABLE_NAME = '{table}' AND INDEX_NAME = 'IX_{table}_Name' AND NON_UNIQUE = 0"));

            // Archived or not, a name differing only in case is the same name.
            var duplicate = await Assert.ThrowsAsync<MySqlException>(() => InsertAsync(context, table, idColumn, "BILLS", archived: "2025-01-01"));
            Assert.Equal(1062, duplicate.Number);
            Assert.Contains($"IX_{table}_Name'", duplicate.Message, StringComparison.Ordinal);
        }
        finally
        {
            await using var server = fixture.CreateOdysseyContext();
            await server.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        }
    }

    private static async Task InsertAsync(OdysseyContext context, string table, string idColumn, string name, string? archived)
    {
        var archivedSql = archived is null ? "NULL" : $"'{archived} 00:00:00'";
        await context.Database.ExecuteSqlRawAsync(
            $"INSERT INTO `{table}` (`{idColumn}`, `Name`, `Archived`) VALUES ('{Guid.NewGuid()}', {{0}}, {archivedSql})", name);
    }

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);
}
