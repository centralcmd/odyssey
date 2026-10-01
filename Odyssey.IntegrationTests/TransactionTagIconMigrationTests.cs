using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// <c>AddTransactionTagIcon</c> (issue #279) applied over a database that already holds tags — the
/// state every production database is in. The column must arrive as <c>varchar(64) NULL</c> with no
/// default and no backfill, so every existing tag reads as the default icon.
/// </summary>
/// <remarks>
/// Unobservable on the fast tiers: the EF InMemory provider has no schema and no migrations.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class TransactionTagIconMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_transaction_tag_icon";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_AddCurrencyForeignKeys";

    private const string Subject = "_AddTransactionTagIcon";

    [SkippableFact]
    public async Task A_pre_existing_tag_survives_the_upgrade_with_no_icon_and_the_column_is_nullable_varchar_64()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        var tagId = Guid.NewGuid();

        try
        {
            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Baseline);
                Assert.Null(await ColumnAsync(context));
                // Raw SQL: the entity already carries Icon, which the baseline schema does not have.
                await MigrationSeam.InsertTransactionTagAsync(context, tagId, "Groceries");
            }

            await using (var context = NewContext())
            {
                await MigrationSeam.MigrateToAsync(context, Subject);

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
