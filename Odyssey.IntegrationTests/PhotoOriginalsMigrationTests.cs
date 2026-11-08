// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a constant this test
// generated itself — there is no external input — and the rows are deliberately written outside the services, so the
// database's own checks are what is under test.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Issue #428 on real MariaDB (PR #441 test review): <c>MovePhotoOriginalsToObjectStore</c> applies over existing
/// (legacy) photos and leaves each one valid, and the constraints it adds — exactly one byte source, a complete original,
/// the RESTRICT key to <c>StoredObjects</c>, the failure-code list and the seeded lease row — hold in the database, where
/// the EF InMemory provider sees none of them.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class PhotoOriginalsMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_photo_originals";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_AddFileAnalysisMatchContextAndPromptAdditions";

    [SkippableFact]
    public async Task Existing_photos_stay_valid_and_legacy_across_the_migration()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();
        var photoId = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        try
        {
            await using var context = NewContext();
            await MigrationSeam.MigrateToAsync(context, Baseline);
            await InsertLegacyAsync(context, photoId, fileId);

            await context.Database.MigrateAsync();

            var row = await MigrationSeam.RowAsync(context,
                $"SELECT `FileId`, `StoredObjectId`, `FileName`, `LegacyFileId` FROM `Photos` WHERE `PhotoId` = '{photoId}'");
            Assert.NotNull(row);
            Assert.Equal(fileId, Guid.Parse(row!["FileId"]!.ToString()!));
            Assert.Null(row["StoredObjectId"]);
            Assert.Null(row["FileName"]);
            Assert.Null(row["LegacyFileId"]);
            Assert.Equal(1L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `PhotoOriginalBackfillLeases` WHERE `Id` = 1"));
        }
        finally
        {
            await DropAsync();
        }
    }

    [SkippableFact]
    public async Task The_database_enforces_one_source_a_complete_original_restrict_and_the_failure_codes()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await context.Database.MigrateAsync();
            var volumeId = Guid.NewGuid();
            var objectId = Guid.NewGuid();
            await context.Database.ExecuteSqlRawAsync(
                $"INSERT INTO `FileStorageVolumes` (`Id`, `Name`, `LastSeenAtUtc`, `ReconciliationPass`, `QuarantinedFileCount`) VALUES ('{volumeId}', 'primary', NOW(), 0, 0)");
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO `StoredObjects` (`Id`, `VolumeId`, `FeatureKey`, `RelativePath`, `SizeBytes`, `Sha256Hash`, `State`, `CreatedAtUtc`, `DeleteImmediately`) "
                + $"VALUES ('{objectId}', '{volumeId}', 'PhotoOriginals', 'photo-originals/2026/1/a.jpg', 3, '{new string('a', 64)}', 1, NOW(), 0)");
            var legacyFile = Guid.NewGuid();
            await InsertFileAsync(context, legacyFile);

            // CK_Photos_ExactlyOneSource: neither, and both, are refused.
            await AssertRefusedAsync(InsertPhotoAsync(context, fileId: null, objectId: null));
            await AssertRefusedAsync(InsertPhotoAsync(context, legacyFile, objectId, complete: true));
            // CK_Photos_OriginalComplete: an object without its name, type, size and hash is refused.
            await AssertRefusedAsync(InsertPhotoAsync(context, fileId: null, objectId, complete: false));
            await InsertPhotoAsync(context, fileId: null, objectId, complete: true);

            // RESTRICT: the object a photo owns cannot be removed under it.
            await AssertRefusedAsync(context.Database.ExecuteSqlRawAsync($"DELETE FROM `StoredObjects` WHERE `Id` = '{objectId}'"));

            // CK_PhotoOriginalBackfillFailures_Code: the four codes, nothing else.
            var parked = Guid.NewGuid();
            await InsertPhotoAsync(context, legacyFile, objectId: null, complete: false, photoId: parked);
            await AssertRefusedAsync(context.Database.ExecuteSqlRawAsync(
                $"INSERT INTO `PhotoOriginalBackfillFailures` (`PhotoId`, `Code`, `RecordedAtUtc`) VALUES ('{parked}', 'bogus', NOW())"));
            await context.Database.ExecuteSqlRawAsync(
                $"INSERT INTO `PhotoOriginalBackfillFailures` (`PhotoId`, `Code`, `RecordedAtUtc`) VALUES ('{parked}', 'error', NOW())");
            // The singleton lease.
            await AssertRefusedAsync(context.Database.ExecuteSqlRawAsync(
                $"INSERT INTO `PhotoOriginalBackfillLeases` (`Id`, `Version`, `ConsecutiveEnvironmentalFailures`, `CopiedSharedCount`) VALUES (2, '{Guid.NewGuid()}', 0, 0)"));
        }
        finally
        {
            await DropAsync();
        }
    }

    private static async Task AssertRefusedAsync(Task write) => await Assert.ThrowsAsync<MySqlException>(() => write);

    private static async Task InsertLegacyAsync(OdysseyContext context, Guid photoId, Guid fileId)
    {
        await InsertFileAsync(context, fileId);
        await context.Database.ExecuteSqlRawAsync(
            $"INSERT INTO `Photos` (`PhotoId`, `FileId`, `CreatedAt`, `UpdatedAt`) VALUES ('{photoId}', '{fileId}', NOW(), NOW())");
    }

    private static async Task InsertFileAsync(OdysseyContext context, Guid fileId)
    {
        var blobId = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync($"INSERT INTO `FileBlob` (`Id`, `Content`) VALUES ('{blobId}', X'FFD8FF')");
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `FileMetadata` (`Id`, `FileName`, `ContentType`, `SizeBytes`, `Sha256Hash`, `FileBlobId`, `UploadedAtUtc`) "
            + $"VALUES ('{fileId}', 'p.jpg', 'image/jpeg', 3, '{new string('b', 64)}', '{blobId}', NOW())");
    }

    private static Task InsertPhotoAsync(OdysseyContext context, Guid? fileId, Guid? objectId, bool complete = false, Guid? photoId = null)
    {
        var original = complete
            ? $"'p.jpg', 'image/jpeg', 3, '{new string('a', 64)}'"
            : "NULL, NULL, NULL, NULL";
        return context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `Photos` (`PhotoId`, `FileId`, `StoredObjectId`, `FileName`, `ContentType`, `SizeBytes`, `Sha256Hash`, `CreatedAt`, `UpdatedAt`) "
            + $"VALUES ('{photoId ?? Guid.NewGuid()}', {Sql(fileId)}, {Sql(objectId)}, {original}, NOW(), NOW())");
    }

    private static string Sql(Guid? id) => id is { } value ? $"'{value}'" : "NULL";

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
