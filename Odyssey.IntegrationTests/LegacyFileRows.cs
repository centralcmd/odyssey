using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.IntegrationTests;

/// <summary>
/// Inserts a database-held file (<c>FileBlob</c> + <c>FileMetadata</c>) by raw SQL, naming only the columns every
/// migration since <c>InitialCreate</c> has. A seam test that stops before <c>MoveFilesToObjectStore</c> cannot add a
/// <see cref="FileMetadata"/> entity: the current model would insert the three stored-object columns that schema
/// does not have yet (issue #448).
/// </summary>
internal static class LegacyFileRows
{
    public static async Task InsertAsync(
        OdysseyContext context, Guid fileId, DateTime uploadedAtUtc,
        string fileName = "doc.pdf", string contentType = "application/pdf", string? uploadedByUserId = null)
    {
        var blobId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO `FileBlob` (`Id`, `Content`) VALUES ({blobId}, {new byte[] { 1, 2, 3 }})");
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO `FileMetadata`
                (`Id`, `UploadedByUserId`, `FileName`, `ContentType`, `SizeBytes`, `Sha256Hash`, `FileBlobId`, `UploadedAtUtc`)
            VALUES ({fileId}, {uploadedByUserId}, {fileName}, {contentType}, {3L}, {Guid.NewGuid().ToString("N")}, {blobId}, {uploadedAtUtc})
            """);
    }
}
