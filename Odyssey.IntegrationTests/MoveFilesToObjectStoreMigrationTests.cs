// EF1002 flags interpolation into ExecuteSqlRawAsync; the only value interpolated is this class's own constant database name.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Issue #448 AC 31 (deferred by #456) on real MariaDB: <c>MoveFilesToObjectStore</c> adds keys FROM <c>FileMetadata</c>
/// (to <c>StoredObjects</c>) and a failure table with no key, but never a key TO <c>FileMetadata</c> — so the set
/// <see cref="FileReferenceCounter.Discover"/> returns is the same before and after it. Checked twice: on the database the
/// migration actually builds, and on the model as of the migration before it and as of the migration itself.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class MoveFilesToObjectStoreMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_move_files";
    private const string Migration = "_MoveFilesToObjectStore";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_AddFileAnalysisJobQueue";

    [SkippableFact]
    public async Task The_reference_counters_set_is_the_same_before_and_after_the_migration()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await MigrationSeam.MigrateToAsync(context, Baseline);
            var before = await ReferencingColumnsAsync(context);
            await MigrationSeam.MigrateToAsync(context, Migration);
            var after = await ReferencingColumnsAsync(context);

            Assert.NotEmpty(before);
            Assert.Equal(before, after);

            // The database's keys are Discover's columns plus the photo's own, which Discover excludes by design.
            var discovered = FileReferenceCounter.Discover(context)
                .Select(r => ColumnOf(context.Model, r.EntityType, r.PropertyName))
                .Append(ColumnOf(context.Model, typeof(Photo), nameof(Photo.FileId)))
                .Order(StringComparer.Ordinal)
                .ToList();
            Assert.Equal(discovered, after);

            // And Discover's rule over the model as each migration left it gives the same set.
            Assert.Equal(DiscoverIn(TargetModel(context, Baseline)), DiscoverIn(TargetModel(context, Migration)));
            Assert.Equal(discovered.Where(c => c != "Photos.FileId"), DiscoverIn(TargetModel(context, Migration)));
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

    /// <summary>The model a migration's designer recorded, finalized as the migrator would.</summary>
    private static IModel TargetModel(OdysseyContext context, string nameSuffix)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[MigrationSeam.IdOf(context, nameSuffix)], context.Database.ProviderName!);
        return context.GetService<IModelRuntimeInitializer>().Initialize(migration.TargetModel!, designTime: true);
    }

    /// <summary>
    /// <see cref="FileReferenceCounter.Discover"/>'s rule — every key to <c>FileMetadata</c> but the photo's own — over a
    /// designer model, whose entity types are named rather than typed, as <c>Table.Column</c>.
    /// </summary>
    private static List<string> DiscoverIn(IModel model) =>
        [.. model.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType.Name == typeof(FileMetadata).FullName)
            .Select(fk =>
            {
                var entity = fk.DeclaringEntityType;
                var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
                return $"{entity.GetTableName()}.{fk.Properties.Single().GetColumnName(table)}";
            })
            .Where(c => c != "Photos.FileId")
            .Distinct()
            .Order(StringComparer.Ordinal)];

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
