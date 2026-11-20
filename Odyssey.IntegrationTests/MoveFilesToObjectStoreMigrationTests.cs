// EF1002 flags interpolation into ExecuteSqlRawAsync; the only value interpolated is this class's own constant database name.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Issue #448 AC 31 (deferred by #456) on real MariaDB, at head: the keys TO <c>FileMetadata</c> in the database are
/// exactly the columns <see cref="FileReferenceCounter.Discover"/> derives from the EF model — so a referencing table
/// the model declares is a key the engine enforces, and the counter can never miss one. (The photo's own
/// <c>Photos.FileId</c> key was dropped by <c>RemoveLegacyFileBlobStorage</c>.)
/// </summary>
/// <remarks>
/// <c>MoveFilesToObjectStore</c>'s own before/after comparison was retired with the other seam tests once production
/// had applied it (see the README).
/// </remarks>
[Collection(MariaDbCollection.Name)]
public class MoveFilesToObjectStoreMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_move_files";

    [SkippableFact]
    public async Task The_keys_to_FileMetadata_are_exactly_the_reference_counters_set()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await fixture.CreateMigratedDatabaseAsync(Database);

        try
        {
            await using var context = NewContext();
            var keys = await ReferencingColumnsAsync(context);

            var discovered = FileReferenceCounter.Discover(context)
                .Select(r => ColumnOf(context.Model, r.EntityType, r.PropertyName))
                .Order(StringComparer.Ordinal)
                .ToList();

            Assert.NotEmpty(keys);
            Assert.Equal(discovered, keys);
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>Every foreign-key column in the live database whose referenced table is <c>FileMetadata</c>, as <c>Table.Column</c>.</summary>
    private static async Task<List<string>> ReferencingColumnsAsync(OdysseyContext context)
    {
        var columns = await context.Database.SqlQuery<string>($"""
            SELECT CONCAT(`TABLE_NAME`, '.', `COLUMN_NAME`) AS `Value` FROM information_schema.KEY_COLUMN_USAGE
            WHERE `TABLE_SCHEMA` = DATABASE() AND `REFERENCED_TABLE_NAME` = 'FileMetadata'
            """).ToListAsync();
        return [.. columns.Order(StringComparer.Ordinal)];
    }

    private static string ColumnOf(IModel model, Type entityType, string propertyName)
    {
        var entity = model.FindEntityType(entityType)!;
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        return $"{entity.GetTableName()}.{entity.FindProperty(propertyName)!.GetColumnName(table)}";
    }

    private OdysseyContext NewContext() =>
        new(new DbContextOptionsBuilder<OdysseyContext>()
            .UseMySql(fixture.ConnectionStringFor(Database), ServerVersion.AutoDetect(fixture.OdysseyConnectionString))
            .Options);

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
