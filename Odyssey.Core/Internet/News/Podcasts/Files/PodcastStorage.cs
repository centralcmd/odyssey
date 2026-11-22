using Odyssey.Context;
using Odyssey.Core.FileStorage;

namespace Odyssey.Core.Internet.News.Podcasts.Files;

/// <summary>
/// The podcast audio feature on the #346 store (issue #556 §3.2):
/// <c>podcast-audio/&lt;feedId:N&gt;/&lt;xx&gt;/&lt;yy&gt;/&lt;storedObjectId:N&gt;.&lt;ext&gt;</c>, sharded by episode id so no folder
/// passes 10,000 entries. Opaque names, so no publisher-supplied name reaches the disk or a database path. Graced like
/// every production feature (user decision): a removed episode stays on disk for the deletion grace.
/// <see cref="PodcastEpisodeAudio"/> is its one referencing table.
/// </summary>
public static class PodcastAudioStorage
{
    public const string FeatureKey = "PodcastAudio";

    public const string RootFolder = "podcast-audio";

    public static readonly StorageFeature Feature = StorageFeature.Create<PodcastAudioLayout>(
        FeatureKey,
        RootFolder,
        args => [args.FeedId.ToString("N"), .. StorageLayout.Shard(args.EpisodeId)],
        StorageReleaseMode.Graced,
        StorageFeatureReference.To<PodcastEpisodeAudio>(a => a.StoredObjectId),
        StorageNamingMode.Opaque);
}

/// <summary>Where one episode's audio is written: its feed and the episode id the shard is taken from.</summary>
public sealed record PodcastAudioLayout(Guid FeedId, Guid EpisodeId);

/// <summary>
/// The podcast artwork feature (issue #556 §3.2): <c>podcast-artwork/&lt;feedId:N&gt;/&lt;storedObjectId:N&gt;[-s|-m].&lt;ext&gt;</c>.
/// A feed holds at most <see cref="Dtos.PodcastLimits.MaxArtworkPerFeed"/> artworks × 3 files, so one folder per feed
/// stays under 10,000 entries. <see cref="PodcastArtworkFile"/> is its one referencing table.
/// </summary>
public static class PodcastArtworkStorage
{
    public const string FeatureKey = "PodcastArtwork";

    public const string RootFolder = "podcast-artwork";

    public static readonly StorageFeature Feature = StorageFeature.Create<PodcastArtworkLayout>(
        FeatureKey,
        RootFolder,
        args => [args.FeedId.ToString("N")],
        StorageReleaseMode.Graced,
        StorageFeatureReference.To<PodcastArtworkFile>(f => (Guid?)f.StoredObjectId),
        StorageNamingMode.Opaque);
}

/// <summary>Where one feed's artwork files are written.</summary>
public sealed record PodcastArtworkLayout(Guid FeedId);
