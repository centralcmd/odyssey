using Odyssey.Context;

namespace Odyssey.Core.FileStorage.Files;

/// <summary>
/// The one way a <see cref="FileMetadata"/> row is removed (issue #448 §3.4): in the same save, its stored object is
/// released — never deleted from under the row (#346 consumer rule) — so the maintenance pass erases it after the
/// deletion grace. A source-lint fails on a <c>FileMetadata</c> removal anywhere else.
/// </summary>
public static class StoredFileRemoval
{
    /// <summary>
    /// Stages the removal on <paramref name="context"/>, <b>without saving</b>. Tracked removals only, never
    /// <c>ExecuteDeleteAsync</c>, so the EF InMemory test tier exercises it. <paramref name="store"/> must work on the same
    /// context (the DI-scoped one); it may be null only where no file can exist.
    /// </summary>
    public static async Task StageRemoveAsync(OdysseyContext context, IObjectStore? store, FileMetadata file, CancellationToken cancellationToken)
    {
        if (store is null)
        {
            throw new FileStorageUnavailableException("File storage is not set up on this server.");
        }

        await store.StageReleaseAsync(StoredFileAreas.StoredObjectIdOf(file), immediate: false, cancellationToken);
        context.FileMetadata.Remove(file);
    }
}
