using Odyssey.Context;
using Odyssey.Core.FileStorage;

namespace Odyssey.Core.Journal.Originals;

/// <summary>
/// Writes a photo original to the store (issue #428 §3.2 steps 4–5, §3.4 step 4): the upload reserve, a bucket slot
/// for the photo's year, then <see cref="IObjectStore.PutAsync"/>. The returned row is attached to the caller's
/// context, <see cref="StoredObjectState.Committed"/> but unsaved: the caller saves it with the photo, or the
/// abandoned-write sweep removes it with its file.
/// </summary>
public sealed class PhotoOriginalWriter(
    IObjectStore store,
    StorageFeatureCatalogue catalogue,
    FileStorageVolumeRegistry volumes,
    IFileStorageSpaceProbe space,
    PhotoOriginalBucketAllocator buckets)
{
    /// <summary>Refuses with <c>507</c> when the feature's volume is below the upload reserve (§7.9).</summary>
    public void EnsureReserve()
    {
        if (catalogue.Find(PhotoOriginalStorage.FeatureKey) is not { } feature)
        {
            throw new FileStorageUnavailableException("Photo storage is not configured.");
        }

        var volume = volumes.ForWrite(feature);
        if (space.Measure(volume.RootPath) is { } measured
            && measured.Free < ObjectStore.FreeSpaceFloorBytes + PhotoOriginalLimits.UploadReserveBytes)
        {
            throw new FileStorageFullException("Photo storage is full.");
        }
    }

    /// <summary>Whether the reserve holds, for a caller that must not throw (the backfill's health gate).</summary>
    public bool HasReserve()
    {
        try
        {
            EnsureReserve();
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    public async Task<StoredObject> PutAsync(
        Stream content, string fileName, string contentType, DateTime createdAtUtc, long maxBytes, CancellationToken cancellationToken)
    {
        EnsureReserve();
        var year = createdAtUtc.Year;
        var bucket = await buckets.AllocateAsync(year, cancellationToken);
        return await store.PutAsync(
            PhotoOriginalStorage.FeatureKey, new PhotoOriginalLayout(year, bucket), fileName, null, contentType,
            content, maxBytes, cancellationToken);
    }
}
