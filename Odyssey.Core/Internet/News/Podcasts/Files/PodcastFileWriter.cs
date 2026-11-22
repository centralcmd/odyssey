using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Internet.News.Files;

namespace Odyssey.Core.Internet.News.Podcasts.Files;

/// <summary>
/// Writes podcast files to the <c>PodcastAudio</c> and <c>PodcastArtwork</c> features (issue #556 §3.2, §6.4). Every
/// write is refused below the NEWS headroom threshold (<see cref="NewsArticleWriter.HeadroomThresholdBytes"/>), so
/// podcast files stop 1 GiB before user uploads do; audio also subtracts what in-flight downloads have reserved.
/// </summary>
public sealed class PodcastFileWriter(
    IObjectStore store,
    StorageFeatureCatalogue catalogue,
    FileStorageVolumeRegistry volumes,
    IFileStorageSpaceProbe space)
{
    /// <summary>
    /// The free bytes of <paramref name="featureKey"/>'s volume, or <c>null</c> when it cannot be measured (the probe
    /// declines); <c>-1</c> when the feature is unbound or its volume unusable.
    /// </summary>
    public long? FreeBytes(string featureKey)
    {
        if (catalogue.Find(featureKey) is not { } feature)
        {
            return -1;
        }

        try
        {
            var volume = volumes.ForWrite(feature);
            return space.Measure(volume.RootPath)?.Free;
        }
        catch (FileStorageUnavailableException)
        {
            return -1;
        }
    }

    /// <summary>Whether a write of <paramref name="expectedBytes"/> beyond <paramref name="reservedBytes"/> keeps the headroom.</summary>
    public bool HasHeadroom(string featureKey, long expectedBytes = 0, long reservedBytes = 0) =>
        FreeBytes(featureKey) switch
        {
            null => true,
            < 0 => false,
            { } free => free - reservedBytes - expectedBytes >= NewsArticleWriter.HeadroomThresholdBytes,
        };

    /// <summary>One artwork file, attached to the caller's context as <c>Committed</c> but unsaved.</summary>
    public Task<StoredObject> PutArtworkAsync(Guid feedId, string? suffix, string contentType, byte[] bytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (!HasHeadroom(PodcastArtworkStorage.FeatureKey, bytes.LongLength))
        {
            throw new NewsHeadroomException();
        }

        return store.PutAsync(
            PodcastArtworkStorage.FeatureKey, new PodcastArtworkLayout(feedId), originalFileName: null, suffix, contentType,
            new MemoryStream(bytes, writable: false), Math.Max(1, bytes.LongLength), cancellationToken);
    }
}
