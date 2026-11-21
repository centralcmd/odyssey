using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The journal, task and photo tag icon columns (issue #345) on real MariaDB: each is <c>varchar(64) NULL</c> with no
/// default, so a tag written without one reads as the default, and a set icon round-trips.
/// </summary>
/// <remarks>
/// Unobservable on the fast tiers: the EF InMemory provider has no schema.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class JournalTaskPhotoTagIconMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_journal_tag_icon";

    private static readonly string[] Tables = ["JournalTags", "JournalTaskTags", "PhotoTags"];

    [SkippableFact]
    public async Task Tags_written_without_an_icon_read_none_and_each_column_is_nullable_varchar_64()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);

        var ids = Tables.ToDictionary(table => table, _ => Guid.NewGuid());

        try
        {
            await using (var context = NewContext())
            {
                // Raw SQL, naming no icon: the columns' own default is what is under test.
                await context.Database.ExecuteSqlAsync(
                    $"INSERT INTO `JournalTags` (`JournalTagId`, `Name`) VALUES ({ids["JournalTags"]}, {"Holidays"})");
                await context.Database.ExecuteSqlAsync(
                    $"INSERT INTO `JournalTaskTags` (`JournalTaskTagId`, `Name`) VALUES ({ids["JournalTaskTags"]}, {"Holidays"})");
                await context.Database.ExecuteSqlAsync(
                    $"INSERT INTO `PhotoTags` (`PhotoTagId`, `Name`) VALUES ({ids["PhotoTags"]}, {"Holidays"})");

                foreach (var table in Tables)
                {
                    var column = await ColumnAsync(context, table);
                    Assert.NotNull(column);
                    Assert.Equal("varchar(64)", column!["COLUMN_TYPE"]?.ToString());
                    Assert.Equal("YES", column["IS_NULLABLE"]?.ToString());
                    // MariaDB reports a nullable column with no default as the literal string "NULL".
                    Assert.True(column["COLUMN_DEFAULT"] is null or "NULL");
                }

                var journal = await context.JournalTags.SingleAsync(t => t.JournalTagId == ids["JournalTags"]);
                var task = await context.JournalTaskTags.SingleAsync(t => t.JournalTaskTagId == ids["JournalTaskTags"]);
                var photo = await context.PhotoTags.SingleAsync(t => t.PhotoTagId == ids["PhotoTags"]);
                Assert.Null(journal.Icon);
                Assert.Null(task.Icon);
                Assert.Null(photo.Icon);
                Assert.Equal("Holidays", photo.Name);

                journal.Icon = "beach_access";
                task.Icon = "checklist";
                photo.Icon = "landscape";
                await context.SaveChangesAsync();
            }

            await using (var context = NewContext())
            {
                Assert.Equal("beach_access", (await context.JournalTags.AsNoTracking().SingleAsync()).Icon);
                Assert.Equal("checklist", (await context.JournalTaskTags.AsNoTracking().SingleAsync()).Icon);
                Assert.Equal("landscape", (await context.PhotoTags.AsNoTracking().SingleAsync()).Icon);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    private static Task<Dictionary<string, object?>?> ColumnAsync(OdysseyContext context, string table) =>
        MigrationSeam.RowAsync(context, $"""
            SELECT COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND COLUMN_NAME = 'Icon'
            """);

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
