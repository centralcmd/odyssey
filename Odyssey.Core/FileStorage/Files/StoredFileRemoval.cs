using Microsoft.EntityFrameworkCore;
using Odyssey.Context;

namespace Odyssey.Core.FileStorage.Files;

/// <summary>
/// The one way a <see cref="FileMetadata"/> row is removed (issue #448 §3.4): in the same save, a legacy file's
/// <c>FileBlob</c> is removed with it, and a file on disk has its stored object released — never deleted from under the
/// row (#346 consumer rule) — so the maintenance pass erases it after the deletion grace. A source-lint fails on a
/// <c>FileMetadata</c> removal anywhere else (the photo move excepted).
/// </summary>
public static class StoredFileRemoval
{
    /// <summary>
    /// Stages the removal on <paramref name="context"/>, <b>without saving</b>. Tracked removals only, never
    /// <c>ExecuteDeleteAsync</c>, so the EF InMemory test tier exercises it. <paramref name="store"/> must work on the same
    /// context (the DI-scoped one); it may be null only where no file on disk can exist.
    /// </summary>
    public static async Task StageRemoveAsync(OdysseyContext context, IObjectStore? store, FileMetadata file, CancellationToken cancellationToken)
    {
        if (StoredFileAreas.StoredObjectIdOf(file) is { } objectId)
        {
            if (store is null)
            {
                throw new FileStorageUnavailableException("File storage is not set up on this server.");
            }

            await store.StageReleaseAsync(objectId, immediate: false, cancellationToken);
        }
        else if (file.FileBlobId is { } blobId)
        {
            var blob = await context.FileBlob.FirstOrDefaultAsync(b => b.Id == blobId, cancellationToken);
            if (blob is not null)
            {
                context.FileBlob.Remove(blob);
            }
        }

        context.FileMetadata.Remove(file);
    }
}
