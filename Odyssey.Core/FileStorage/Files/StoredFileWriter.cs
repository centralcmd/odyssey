using Odyssey.Context;

namespace Odyssey.Core.FileStorage.Files;

/// <summary>
/// Writes a Files-store file to the object store (issue #448 §3.3): the degraded and reserve checks first, then a bucket
/// slot for the file's year, then <see cref="IObjectStore.PutAsync"/>. Checking before the slot means a <c>503</c> or
/// <c>507</c> never burns one (the <c>PhotoOriginalWriter</c> order). The returned row is attached to the caller's context,
/// <see cref="StoredObjectState.Committed"/> but unsaved: the caller saves it with its <c>FileMetadata</c>, or the
/// abandoned-write sweep removes it with its file.
/// </summary>
public sealed class StoredFileWriter(
    IObjectStore store,
    StorageFeatureCatalogue catalogue,
    FileStorageVolumeRegistry volumes,
    IFileStorageSpaceProbe space,
    StoredFileBucketAllocator buckets,
    TimeProvider time)
{
    /// <summary>
    /// Refuses with <c>503</c> when the area's volume is not configured or is <c>Degraded</c>, and with <c>507</c> when it
    /// is below the upload reserve (§7.9).
    /// </summary>
    public void EnsureWritable(StoredFileArea area)
    {
        if (catalogue.Find(StoredFileAreas.FeatureKey(area)) is not { } feature)
        {
            throw new FileStorageUnavailableException("File storage is not configured.");
        }

        var volume = volumes.ForWrite(feature);
        if (space.Measure(volume.RootPath) is { } measured && measured.Free < FileStorageReserves.UploadThresholdBytes)
        {
            throw new FileStorageFullException("File storage is full.");
        }
    }

    /// <summary>Whether the area can be written right now, for a caller that must not throw.</summary>
    public bool CanWrite(StoredFileArea area)
    {
        try
        {
            EnsureWritable(area);
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    /// <summary>The bytes onto <paramref name="area"/>'s feature, under the UTC year of <paramref name="uploadedAtUtc"/>.</summary>
    public async Task<StoredObject> PutAsync(
        StoredFileArea area, Stream content, string fileName, string contentType, DateTime uploadedAtUtc, long maxBytes,
        CancellationToken cancellationToken)
    {
        EnsureWritable(area);
        var key = StoredFileAreas.FeatureKey(area);
        var year = uploadedAtUtc.Year;
        var bucket = await buckets.AllocateAsync(key, year, cancellationToken);
        return await store.PutAsync(key, new StoredFileLayout(year, bucket), fileName, null, contentType, content, maxBytes, cancellationToken);
    }

    /// <summary>
    /// <see cref="StageNewFileAsync"/> split in two (issue #521 v5 §1): the bytes are written and reserved here, outside
    /// any transaction, and <see cref="AttachPrepared"/> stages a fresh <see cref="FileMetadata"/> per attempt of a
    /// retried one. The file's id and upload time are fixed now, so every attempt names the same file.
    /// </summary>
    public async Task<PreparedStoredFile> PrepareNewFileAsync(
        StoredFileArea area, Stream content, string fileName, string contentType, long maxBytes, CancellationToken cancellationToken)
    {
        EnsureWritable(area);
        var now = time.GetUtcNow().UtcDateTime;
        var key = StoredFileAreas.FeatureKey(area);
        var bucket = await buckets.AllocateAsync(key, now.Year, cancellationToken);
        var reservation = await store.ReserveAndWriteAsync(
            key, new StoredFileLayout(now.Year, bucket), fileName, null, contentType, content, maxBytes, cancellationToken);
        return new PreparedStoredFile(Guid.NewGuid(), area, reservation, fileName, contentType, now);
    }

    /// <summary>
    /// Stages <paramref name="prepared"/> on <paramref name="context"/>: a new <see cref="FileMetadata"/> and its stored
    /// object attached <c>Committed</c>, unsaved. Call once per attempt, on a cleared tracker.
    /// </summary>
    public FileMetadata AttachPrepared(OdysseyContext context, PreparedStoredFile prepared, string? userId)
    {
        var stored = store.AttachReserved(context, prepared.Reservation);
        var file = new FileMetadata
        {
            Id = prepared.FileId,
            UploadedByUserId = userId,
            FileName = prepared.FileName,
            ContentType = prepared.ContentType,
            SizeBytes = stored.SizeBytes,
            Sha256Hash = stored.Sha256Hash!,
            UploadedAtUtc = prepared.UploadedAtUtc,
        };
        StoredFileAreas.Assign(file, prepared.Area, stored.Id);
        context.FileMetadata.Add(file);
        return file;
    }

    /// <summary>Releases a prepared file that will never be attached (issue #521 implementation note 2).</summary>
    public Task AbandonPreparedAsync(PreparedStoredFile prepared, CancellationToken cancellationToken) =>
        store.AbandonReservationAsync(prepared.Reservation, cancellationToken);

    /// <summary>
    /// Writes a new file and stages its <see cref="FileMetadata"/> on the caller's context, <b>without saving</b>: one
    /// <c>SaveChangesAsync</c> by the caller commits the row and its stored object together (§3.3 step 5). Size and hash
    /// come from the store's measurement of the bytes it wrote, never the caller. <c>now</c> is taken once, so the folder
    /// year and <c>UploadedAtUtc</c> never disagree.
    /// </summary>
    public async Task<FileMetadata> StageNewFileAsync(
        OdysseyContext context, StoredFileArea area, Stream content, string fileName, string contentType,
        string? userId, string? description, long maxBytes, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var stored = await PutAsync(area, content, fileName, contentType, now, maxBytes, cancellationToken);
        var file = new FileMetadata
        {
            Id = Guid.NewGuid(),
            UploadedByUserId = userId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = stored.SizeBytes,
            Sha256Hash = stored.Sha256Hash!,
            Description = description?.Length > 256 ? description[..256] : description,
            UploadedAtUtc = now,
        };
        StoredFileAreas.Assign(file, area, stored.Id);
        context.FileMetadata.Add(file);
        return file;
    }
}

/// <summary>A file written and reserved by <see cref="StoredFileWriter.PrepareNewFileAsync"/>, not yet a <see cref="FileMetadata"/>.</summary>
public sealed record PreparedStoredFile(
    Guid FileId, StoredFileArea Area, StoredObjectReservation Reservation, string FileName, string ContentType, DateTime UploadedAtUtc)
{
    public long SizeBytes => Reservation.SizeBytes;

    public string Sha256Hash => Reservation.Sha256Hash;
}
