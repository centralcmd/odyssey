using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// The transaction-tag icon column (issue #279) on real MariaDB: <c>varchar(64) NULL</c> with no default, so a
/// tag written without one reads as the default icon, and a set icon round-trips.
/// </summary>
/// <remarks>
/// Unobservable on the fast tiers: the EF InMemory provider has no schema.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class TransactionTagIconMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_transaction_tag_icon";

    [SkippableFact]
    public async Task A_tag_written_without_an_icon_reads_none_and_the_column_is_nullable_varchar_64()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);

        var tagId = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                // Raw SQL, naming no icon: the column's own default is what is under test.
                await context.Database.ExecuteSqlAsync(
                    $"INSERT INTO `TransactionTags` (`TransactionTagId`, `Name`) VALUES ({tagId}, {"Groceries"})");

                var column = await ColumnAsync(context);
                Assert.NotNull(column);
                Assert.Equal("varchar(64)", column!["COLUMN_TYPE"]?.ToString());
                Assert.Equal("YES", column["IS_NULLABLE"]?.ToString());
                // MariaDB reports a nullable column with no default as the literal string "NULL".
                Assert.True(column["COLUMN_DEFAULT"] is null or "NULL");

                var tag = await context.TransactionTags.AsNoTracking().SingleAsync(t => t.TransactionTagId == tagId);
                Assert.Null(tag.Icon);
                Assert.Equal("Groceries", tag.Name);

                tag.Icon = "shopping_cart";
                context.TransactionTags.Update(tag);
                await context.SaveChangesAsync();
            }

            await using (var context = NewContext())
            {
                var tag = await context.TransactionTags.AsNoTracking().SingleAsync(t => t.TransactionTagId == tagId);
                Assert.Equal("shopping_cart", tag.Icon);
            }
        }
        finally
        {
            await DropAsync();
        }
    }

    private static Task<Dictionary<string, object?>?> ColumnAsync(OdysseyContext context) =>
        MigrationSeam.RowAsync(context, """
            SELECT COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'TransactionTags' AND COLUMN_NAME = 'Icon'
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
