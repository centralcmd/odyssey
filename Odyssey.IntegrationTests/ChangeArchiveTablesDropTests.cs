using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Issue #78: the migrated schema carries neither of issue #75's two archive ledgers.
///
/// <para>
/// Real MariaDB, because the assertion is about a SCHEMA — neither table is an EF entity, and the
/// InMemory provider has no migrations and no <c>information_schema</c>, so it would report this green
/// whatever the migrations did.
/// </para>
/// </summary>
/// <remarks>
/// The drop's data and <c>Down</c> paths shipped before production went live and are no longer tested:
/// they will never run over real data again. What stays is the property every database must have: no
/// archive table survives the full chain.
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class ChangeArchiveTablesDropTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_change_archive_drop";

    private static readonly string[] ArchiveTables = ["_BudgetItemLabelArchive", "_TransactionTagNameDisambiguation"];

    /// <summary>AC 1. Applying every migration leaves neither table behind.</summary>
    [SkippableFact]
    public async Task Applying_every_migration_leaves_neither_archive_table()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        var connection = await fixture.CreateMigratedDatabaseAsync(Database);

        try
        {
            await using var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
                .UseMySql(connection, ServerVersion.AutoDetect(connection))
                .Options);

            foreach (var table in ArchiveTables)
            {
                Assert.False(await MigrationSeam.TableExistsAsync(context, table), $"{table} survived the migrations.");
            }
        }
        finally
        {
            await using var admin = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>()
                .UseMySql(fixture.OdysseyConnectionString, ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
                .Options);
            await admin.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS `{Database}`");
        }
    }
}
