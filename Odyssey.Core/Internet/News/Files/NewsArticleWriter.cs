using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Dtos;

namespace Odyssey.Core.Internet.News.Files;

/// <summary>The news headroom threshold refused a write (§6.3): a transient skip, and the job holds <c>StorageUnavailable</c>.</summary>
public sealed class NewsHeadroomException() : Exception("The news volume is below its headroom threshold.");

/// <summary>
/// Writes an article's files to the <c>NewsArticles</c> feature (issue #470 §3.2, §6.3). Every write first checks the
/// NEWS headroom — the upload threshold plus <see cref="NewsLimits.ReserveMarginBytes"/> — so news stops writing 1 GiB
/// before user uploads stop, never after. The returned row is attached to the caller's context,
/// <see cref="StoredObjectState.Committed"/> but unsaved: the caller saves it with its <see cref="NewsArticleFile"/>
/// row, or the abandoned-write sweep removes it with its file. Names are opaque, so nothing a publisher supplied
/// reaches a path.
/// </summary>
public sealed class NewsArticleWriter(
    IObjectStore store,
    StorageFeatureCatalogue catalogue,
    FileStorageVolumeRegistry volumes,
    IFileStorageSpaceProbe space)
{
    /// <summary>The free space below which news writes stop.</summary>
    public const long HeadroomThresholdBytes = FileStorageReserves.UploadThresholdBytes + NewsLimits.ReserveMarginBytes;

    /// <summary>Whether a news write would be admitted now: the feature bound, its volume usable and above the threshold.</summary>
    public bool HasHeadroom()
    {
        if (catalogue.Find(NewsArticleStorage.FeatureKey) is not { } feature)
        {
            return false;
        }

        try
        {
            var volume = volumes.ForWrite(feature);
            return space.Measure(volume.RootPath) is not { } measured || measured.Free >= HeadroomThresholdBytes;
        }
        catch (FileStorageUnavailableException)
        {
            return false;
        }
    }

    public Task<StoredObject> PutAsync(NewsArticleLayout layout, string contentType, byte[] bytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(bytes);
        if (!HasHeadroom())
        {
            throw new NewsHeadroomException();
        }

        return store.PutAsync(
            NewsArticleStorage.FeatureKey, layout, originalFileName: null, suffix: null, contentType,
            new MemoryStream(bytes, writable: false), Math.Max(1, bytes.LongLength), cancellationToken);
    }
}
