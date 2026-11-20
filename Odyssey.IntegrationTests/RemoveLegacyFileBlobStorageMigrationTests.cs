// EF1002 flags interpolation into ExecuteSqlRawAsync. Every value interpolated below is a Guid or a constant this test
// generated itself — there is no external input — and the rows are deliberately written outside the services, at a
// schema the current model no longer describes.
#pragma warning disable EF1002

using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.MigrationService;
using Xunit;

namespace Odyssey.IntegrationTests;

/// <summary>
/// <c>RemoveLegacyFileBlobStorage</c> on real MariaDB. Its guard runs before any drop — MariaDB commits DDL implicitly,
/// so a refusal after the first drop could not be rolled back — and refuses while any file is left in the old shape: a
/// photo wrapping a Files-store file, a file whose bytes are in <c>FileBlob</c>, or a file the move parked. On a
/// database that finished both moves it deletes the moves' run history and drops the legacy schema.
/// </summary>
[Collection(MariaDbCollection.Name)]
public class RemoveLegacyFileBlobStorageMigrationTests(MariaDbFixture fixture)
{
    private const string Database = "odyssey_remove_legacy_files";
    private const string Migration = "_RemoveLegacyFileBlobStorage";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string Baseline = "_StoreFetchedRatesInPublishedDirectionOnly";

    public static TheoryData<string, string> LegacyShapes => new()
    {
        { "photo", "Photos.FileId is set" },
        { "file", "FileMetadata.FileBlobId is set" },
        { "parked", "FileBlobBackfillFailures has rows" },
    };

    [SkippableTheory]
    [MemberData(nameof(LegacyShapes))]
    public async Task The_migration_refuses_before_dropping_anything_while_a_legacy_row_is_left(string shape, string named)
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await MigrationSeam.MigrateToAsync(context, Baseline);
            switch (shape)
            {
                case "photo":
                    await InsertLegacyPhotoAsync(context);
                    break;
                case "file":
                    await InsertLegacyFileAsync(context, Guid.NewGuid());
                    break;
                default:
                    await context.Database.ExecuteSqlRawAsync(
                        $"INSERT INTO `FileBlobBackfillFailures` (`FileId`, `Code`, `RecordedAtUtc`) VALUES ('{Guid.NewGuid()}', 'hash', NOW())");
                    break;
            }

            var refused = await Assert.ThrowsAsync<MySqlException>(() => MigrationSeam.MigrateToAsync(context, Migration));

            Assert.Contains("RemoveLegacyFileBlobStorage refused", refused.Message, StringComparison.Ordinal);
            Assert.Contains(named, refused.Message, StringComparison.Ordinal);
            // Nothing was dropped, and the migration is not recorded: the release before can still finish the moves.
            Assert.False(await MigrationSeam.HasRunAsync(context, Migration));
            Assert.True(await MigrationSeam.TableExistsAsync(context, "FileBlob"));
            Assert.True(await MigrationSeam.TableExistsAsync(context, "FileBlobBackfillFailures"));
            Assert.Equal(1L, await MigrationSeam.CountAsync(context,
                $"SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{Database}' AND TABLE_NAME = 'Photos' AND COLUMN_NAME = 'FileId'"));
        }
        finally
        {
            await DropAsync();
        }
    }

    /// <summary>
    /// A database that finished both moves upgrades through the migrations job's runner — its drift guard included,
    /// which must not take the re-added CHECKs for an interrupted run — and loses only what the moves left behind.
    /// </summary>
    [SkippableFact]
    public async Task A_database_that_finished_both_moves_upgrades_and_loses_only_the_moves_history()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        await RecreateAsync();

        try
        {
            await using var context = NewContext();
            await MigrationSeam.MigrateToAsync(context, Baseline);
            var photoId = await InsertMovedPhotoAsync(context);
            foreach (var jobType in new[] { 2, 5, 7 })
            {
                await context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO `JobRuns` (`JobRunId`, `JobType`, `Trigger`, `Status`, `StartedAtUtc`, `FinishedAtUtc`, `LastActivityAtUtc`, `Generation`) "
                    + $"VALUES ('{Guid.NewGuid()}', {jobType}, 0, 1, NOW(), NOW(), NOW(), UUID())");
                await context.Database.ExecuteSqlRawAsync(
                    $"INSERT INTO `JobHeartbeats` (`JobType`, `WorkerId`, `LastTickAtUtc`) VALUES ({jobType}, UUID(), NOW())");
            }

            await MigrationRunner.MigrateAsync(context, CancellationToken.None);

            Assert.True(await MigrationSeam.HasRunAsync(context, Migration));
            Assert.Equal(0L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `JobRuns` WHERE `JobType` IN (5, 7)"));
            Assert.Equal(0L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `JobHeartbeats` WHERE `JobType` IN (5, 7)"));
            Assert.Equal(1L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `JobRuns` WHERE `JobType` = 2"));
            Assert.Equal(1L, await MigrationSeam.CountAsync(context, "SELECT COUNT(*) FROM `JobHeartbeats` WHERE `JobType` = 2"));
            foreach (var table in new[] { "FileBlob", "FileBlobBackfillFailures", "FileBlobBackfillLeases", "PhotoOriginalBackfillFailures", "PhotoOriginalBackfillLeases" })
            {
                Assert.False(await MigrationSeam.TableExistsAsync(context, table), table);
            }

            // The moved photo is untouched, and its original columns are now required.
            Assert.Equal(photoId, await context.Photos.AsNoTracking().Select(p => p.PhotoId).SingleAsync());
            Assert.Equal("NO", await MigrationSeam.ScalarAsync(context,
                $"SELECT IS_NULLABLE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = '{Database}' AND TABLE_NAME = 'Photos' AND COLUMN_NAME = 'StoredObjectId'"));
            // A file must now name exactly one area's stored object.
            await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlRawAsync(
                "INSERT INTO `FileMetadata` (`Id`, `FileName`, `ContentType`, `SizeBytes`, `Sha256Hash`, `UploadedAtUtc`) "
                + $"VALUES ('{Guid.NewGuid()}', 'a.pdf', 'application/pdf', 3, '{new string('c', 64)}', NOW())"));
        }
        finally
        {
            await DropAsync();
        }
    }

    private static async Task InsertLegacyPhotoAsync(OdysseyContext context)
    {
        var fileId = Guid.NewGuid();
        await InsertLegacyFileAsync(context, fileId);
        await context.Database.ExecuteSqlRawAsync(
            $"INSERT INTO `Photos` (`PhotoId`, `FileId`, `CreatedAt`, `UpdatedAt`) VALUES ('{Guid.NewGuid()}', '{fileId}', NOW(), NOW())");
    }

    private static async Task InsertLegacyFileAsync(OdysseyContext context, Guid fileId)
    {
        var blobId = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync($"INSERT INTO `FileBlob` (`Id`, `Content`) VALUES ('{blobId}', X'FFD8FF')");
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `FileMetadata` (`Id`, `FileName`, `ContentType`, `SizeBytes`, `Sha256Hash`, `FileBlobId`, `UploadedAtUtc`) "
            + $"VALUES ('{fileId}', 'p.jpg', 'image/jpeg', 3, '{new string('b', 64)}', '{blobId}', NOW())");
    }

    /// <summary>A photo as the move left it: its original on the store, no wrapped file, its former file id kept.</summary>
    private static async Task<Guid> InsertMovedPhotoAsync(OdysseyContext context)
    {
        var volumeId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var photoId = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync(
            $"INSERT INTO `FileStorageVolumes` (`Id`, `Name`, `LastSeenAtUtc`, `ReconciliationPass`, `QuarantinedFileCount`) VALUES ('{volumeId}', 'primary', NOW(), 0, 0)");
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `StoredObjects` (`Id`, `VolumeId`, `FeatureKey`, `RelativePath`, `SizeBytes`, `Sha256Hash`, `State`, `CreatedAtUtc`, `DeleteImmediately`) "
            + $"VALUES ('{objectId}', '{volumeId}', 'PhotoOriginals', 'photo-originals/2026/1/a.jpg', 3, '{new string('a', 64)}', 1, NOW(), 0)");
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `Photos` (`PhotoId`, `StoredObjectId`, `FileName`, `ContentType`, `SizeBytes`, `Sha256Hash`, `LegacyFileId`, `CreatedAt`, `UpdatedAt`) "
            + $"VALUES ('{photoId}', '{objectId}', 'a.jpg', 'image/jpeg', 3, '{new string('a', 64)}', '{Guid.NewGuid()}', NOW(), NOW())");
        return photoId;
    }

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
