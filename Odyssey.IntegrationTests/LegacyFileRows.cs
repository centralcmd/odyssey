using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.IntegrationTests;

/// <summary>
/// A file row for a seam test, by raw SQL, in whichever shape the schema it runs at has. Below
/// <c>RemoveLegacyFileBlobStorage</c> that is a database-held file (<c>FileBlob</c> + <c>FileMetadata.FileBlobId</c>),
/// naming only the columns every migration since <c>InitialCreate</c> has: a seam test that stops before
/// <c>MoveFilesToObjectStore</c> cannot add a <see cref="FileMetadata"/> entity, since the current model would insert
/// columns that schema does not have yet (issue #448). At head it is a file on the store.
/// </summary>
internal static class LegacyFileRows
{
    public static async Task InsertAsync(
        OdysseyContext context, Guid fileId, DateTime uploadedAtUtc,
        string fileName = "doc.pdf", string contentType = "application/pdf", string? uploadedByUserId = null)
    {
        if (!await MigrationSeam.TableExistsAsync(context, "FileBlob"))
        {
            var objectId = await InsertStoredObjectAsync(context, fileId);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO `FileMetadata`
                    (`Id`, `UploadedByUserId`, `FileName`, `ContentType`, `SizeBytes`, `Sha256Hash`, `FinanceStoredObjectId`, `UploadedAtUtc`)
                VALUES ({fileId}, {uploadedByUserId}, {fileName}, {contentType}, {3L}, {Guid.NewGuid().ToString("N")}, {objectId}, {uploadedAtUtc})
                """);
            return;
        }

        var blobId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO `FileBlob` (`Id`, `Content`) VALUES ({blobId}, {new byte[] { 1, 2, 3 }})");
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO `FileMetadata`
                (`Id`, `UploadedByUserId`, `FileName`, `ContentType`, `SizeBytes`, `Sha256Hash`, `FileBlobId`, `UploadedAtUtc`)
            VALUES ({fileId}, {uploadedByUserId}, {fileName}, {contentType}, {3L}, {Guid.NewGuid().ToString("N")}, {blobId}, {uploadedAtUtc})
            """);
    }

    /// <summary>
    /// The worker's file move, done at the last moment: an interceptor that, just before
    /// <c>RemoveLegacyFileBlobStorage</c>'s guard runs, gives every database-held file the end state the move left it in
    /// (a stored object in the finance area, no blob) on the migration's own connection and transaction. A seam test that
    /// seeded files below <c>MoveFilesToObjectStore</c> and then migrates to head adds it to its context, so the guard —
    /// rightly — finds nothing to refuse, and every migration in between still runs under the runner under test.
    /// </summary>
    public sealed class FinishTheFileMoveBeforeTheGuard : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private const string Marker = "RemoveLegacyFileBlobStorage refused";

        // No user variables: MySqlConnector reads an @name as a parameter unless the connection allows them.
        private static string FinishTheMove()
        {
            var volume = Guid.NewGuid();
            return $"""
                INSERT INTO `FileStorageVolumes` (`Id`, `Name`, `LastSeenAtUtc`, `ReconciliationPass`, `QuarantinedFileCount`)
                SELECT '{volume}', 'm{volume:N}', UTC_TIMESTAMP(), 0, 0
                FROM DUAL WHERE EXISTS (SELECT 1 FROM `FileMetadata` WHERE `FileBlobId` IS NOT NULL);
                INSERT INTO `StoredObjects` (`Id`, `VolumeId`, `FeatureKey`, `RelativePath`, `SizeBytes`, `Sha256Hash`, `State`, `CreatedAtUtc`, `DeleteImmediately`)
                SELECT UUID(), '{volume}', 'FinanceFiles', CONCAT('finance-files/2026/1/', REPLACE(`Id`, '-', ''), '.pdf'),
                       `SizeBytes`, REPEAT('a', 64), 1, UTC_TIMESTAMP(), 0
                FROM `FileMetadata` WHERE `FileBlobId` IS NOT NULL;
                UPDATE `FileMetadata` f
                JOIN `StoredObjects` o ON o.`RelativePath` = CONCAT('finance-files/2026/1/', REPLACE(f.`Id`, '-', ''), '.pdf')
                SET f.`FinanceStoredObjectId` = o.`Id`, f.`FileBlobId` = NULL
                WHERE f.`FileBlobId` IS NOT NULL;
                DELETE FROM `FileBlob`;
                """;
        }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(Marker, StringComparison.Ordinal))
            {
                await using var finish = command.Connection!.CreateCommand();
                finish.Transaction = command.Transaction;
                finish.CommandText = FinishTheMove();
                await finish.ExecuteNonQueryAsync(cancellationToken);
            }

            return result;
        }

        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> NonQueryExecuting(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result)
        {
            if (command.CommandText.Contains(Marker, StringComparison.Ordinal))
            {
                using var finish = command.Connection!.CreateCommand();
                finish.Transaction = command.Transaction;
                finish.CommandText = FinishTheMove();
                finish.ExecuteNonQuery();
            }

            return result;
        }
    }

    private static async Task<Guid> InsertStoredObjectAsync(OdysseyContext context, Guid fileId)
    {
        var volumeId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var volumeName = $"t{volumeId:N}"[..20];
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO `FileStorageVolumes` (`Id`, `Name`, `LastSeenAtUtc`, `ReconciliationPass`, `QuarantinedFileCount`) VALUES ({volumeId}, {volumeName}, {DateTime.UtcNow}, {0}, {0})");
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO `StoredObjects` (`Id`, `VolumeId`, `FeatureKey`, `RelativePath`, `SizeBytes`, `Sha256Hash`, `State`, `CreatedAtUtc`, `DeleteImmediately`)
            VALUES ({objectId}, {volumeId}, {"FinanceFiles"}, {$"finance-files/2026/1/{fileId:N}.pdf"}, {3L}, {new string('a', 64)}, {1}, {DateTime.UtcNow}, {false})
            """);
        return objectId;
    }
}
