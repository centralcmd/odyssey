using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Journal.Originals;

namespace Odyssey.Testing.FileStorage;

/// <summary>
/// Files and photos as production writes them: the bytes on the object store, the row naming them. Every file has its
/// bytes on the store since the legacy database copy was removed, so a fixture can no longer stage a bare row with a
/// blob beside it.
/// </summary>
public static class StoredFileFixtures
{
    /// <summary>
    /// Writes <paramref name="content"/> into <paramref name="area"/> and stages its <see cref="FileMetadata"/> on the
    /// scope's context, <b>unsaved</b>: the caller saves it with whatever references it.
    /// </summary>
    public static Task<FileMetadata> StageFileAsync(
        IServiceProvider scope, byte[] content, string fileName = "file.pdf", string contentType = "application/pdf",
        StoredFileArea area = StoredFileArea.Finance, Guid? id = null, string? uploadedByUserId = null,
        DateTime? uploadedAtUtc = null, string? description = null) =>
        StageFileAsync(
            scope.GetRequiredService<OdysseyContext>(), scope.GetRequiredService<StoredFileWriter>(), content, fileName,
            contentType, area, id, uploadedByUserId, uploadedAtUtc, description);

    /// <inheritdoc cref="StageFileAsync(IServiceProvider, byte[], string, string, StoredFileArea, Guid?, string?, DateTime?, string?)"/>
    /// <remarks><paramref name="writer"/> must work on <paramref name="context"/> (a <see cref="TestFileStore"/> made for it).</remarks>
    public static async Task<FileMetadata> StageFileAsync(
        OdysseyContext context, StoredFileWriter writer, byte[] content, string fileName = "file.pdf",
        string contentType = "application/pdf", StoredFileArea area = StoredFileArea.Finance, Guid? id = null,
        string? uploadedByUserId = null, DateTime? uploadedAtUtc = null, string? description = null)
    {
        var uploadedAt = uploadedAtUtc ?? DateTime.UtcNow;
        var stored = await writer.PutAsync(area, new MemoryStream(content), fileName, contentType, uploadedAt, content.LongLength, default);
        var file = new FileMetadata
        {
            Id = id ?? Guid.NewGuid(),
            UploadedByUserId = uploadedByUserId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = content.LongLength,
            Sha256Hash = Sha256(content),
            Description = description,
            UploadedAtUtc = uploadedAt,
        };
        StoredFileAreas.Assign(file, area, stored.Id);
        context.FileMetadata.Add(file);
        return file;
    }

    /// <summary><see cref="StageFileAsync(IServiceProvider, byte[], string, string, StoredFileArea, Guid?, string?, DateTime?, string?)"/>, saved.</summary>
    public static async Task<FileMetadata> SeedFileAsync(
        IServiceProvider scope, byte[] content, string fileName = "file.pdf", string contentType = "application/pdf",
        StoredFileArea area = StoredFileArea.Finance, Guid? id = null, string? uploadedByUserId = null,
        DateTime? uploadedAtUtc = null, string? description = null)
    {
        var file = await StageFileAsync(scope, content, fileName, contentType, area, id, uploadedByUserId, uploadedAtUtc, description);
        await scope.GetRequiredService<OdysseyContext>().SaveChangesAsync();
        return file;
    }

    /// <summary>
    /// Writes <paramref name="content"/> into <paramref name="area"/> through the scope's writer and returns the stored
    /// object's id, attached to the scope's context but <b>unsaved</b>: the caller names it from the row it saves.
    /// </summary>
    public static async Task<Guid> PutAsync(
        IServiceProvider scope, byte[] content, StoredFileArea area = StoredFileArea.Finance, string fileName = "file.pdf",
        string contentType = "application/pdf") =>
        (await scope.GetRequiredService<StoredFileWriter>()
            .PutAsync(area, new MemoryStream(content), fileName, contentType, DateTime.UtcNow, content.LongLength, default)).Id;

    /// <summary>
    /// A metadata-only row for a test that never reads the bytes (a list, a link, an authorization check): it names a
    /// stored object id that exists nowhere, which only the EF InMemory provider, with no keys, accepts.
    /// </summary>
    public static FileMetadata Row(
        Guid? id = null, string fileName = "file.pdf", string contentType = "application/pdf", long sizeBytes = 1,
        string? sha256Hash = null, string? uploadedByUserId = null, DateTime? uploadedAtUtc = null,
        StoredFileArea area = StoredFileArea.Finance, string? description = null)
    {
        var file = new FileMetadata
        {
            Id = id ?? Guid.NewGuid(),
            UploadedByUserId = uploadedByUserId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Sha256Hash = sha256Hash ?? Guid.NewGuid().ToString("N"),
            Description = description,
            UploadedAtUtc = uploadedAtUtc ?? DateTime.UtcNow,
        };
        StoredFileAreas.Assign(file, area, Guid.NewGuid());
        return file;
    }

    /// <summary>
    /// Writes <paramref name="content"/> as a photo original and stages the photo on the scope's context, <b>unsaved</b>.
    /// </summary>
    public static async Task<Photo> StagePhotoAsync(
        IServiceProvider scope, byte[] content, string fileName = "p.jpg", string contentType = "image/jpeg",
        string? userId = null, DateTime? takenAt = null, DateTime? createdAt = null, Guid? photoId = null)
    {
        var now = createdAt ?? PhotoTestData.DefaultCreatedAt;
        var stored = await scope.GetRequiredService<PhotoOriginalWriter>()
            .PutAsync(new MemoryStream(content), fileName, contentType, now, content.LongLength, default);
        var photo = new Photo
        {
            PhotoId = photoId ?? Guid.NewGuid(),
            StoredObjectId = stored.Id,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = content.LongLength,
            Sha256Hash = Sha256(content),
            TakenAt = takenAt,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        scope.GetRequiredService<OdysseyContext>().Photos.Add(photo);
        return photo;
    }

    /// <summary>
    /// For a real-engine test that never reads the bytes: a <c>Committed</c> stored-object row (with a volume row of its
    /// own) for a file's RESTRICT key to point at, staged on <paramref name="context"/> unsaved. Remove it with
    /// <see cref="RemoveStoredObjectRowAsync"/> once the file row is gone.
    /// </summary>
    public static Guid AddStoredObjectRow(OdysseyContext context, Guid? id = null, string featureKey = StoredFileAreas.FinanceFeatureKey)
    {
        var volume = new FileStorageVolume { Id = Guid.NewGuid(), Name = $"t{Guid.NewGuid():N}"[..20], LastSeenAtUtc = DateTime.UtcNow };
        var stored = new StoredObject
        {
            Id = id ?? Guid.NewGuid(),
            VolumeId = volume.Id,
            FeatureKey = featureKey,
            RelativePath = $"test/{Guid.NewGuid():N}.pdf",
            SizeBytes = 3,
            Sha256Hash = Sha256([1, 2, 3]),
            State = StoredObjectState.Committed,
            CreatedAtUtc = DateTime.UtcNow,
        };
        context.Set<FileStorageVolume>().Add(volume);
        context.Set<StoredObject>().Add(stored);
        return stored.Id;
    }

    /// <summary>Removes a row <see cref="AddStoredObjectRow"/> added, and its volume (relational providers only).</summary>
    public static async Task RemoveStoredObjectRowAsync(OdysseyContext context, Guid id)
    {
        var volumeId = await context.Set<StoredObject>().Where(o => o.Id == id).Select(o => (Guid?)o.VolumeId).SingleOrDefaultAsync();
        await context.Set<StoredObject>().Where(o => o.Id == id).ExecuteDeleteAsync();
        if (volumeId is { } volume)
        {
            await context.Set<FileStorageVolume>().Where(v => v.Id == volume).ExecuteDeleteAsync();
        }
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
