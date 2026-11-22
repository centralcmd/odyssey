using System.Text.Json.Serialization;

namespace Odyssey.Dtos.Internet;

// Podcasts in the news reader (issue #556). Wire enums serialise by name through TolerantEnumConverter, so a client
// built before a member was added reads it as Unknown rather than failing. Stored ordinals are a persistence contract
// and are never renumbered.

/// <summary>What a feed carries (§4.1). <see cref="Article"/> is ordinal <c>0</c>, so every feed predating podcasts is one.</summary>
[JsonConverter(typeof(TolerantEnumConverter<NewsFeedKind>))]
public enum NewsFeedKind : byte
{
    Article = 0,
    Podcast = 1,
    Unknown = byte.MaxValue,
}

/// <summary>The <c>kind</c> filter of the feed list and catalogue (§5.1 #4).</summary>
public enum NewsFeedKindFilter : byte
{
    All = 0,
    Article = 1,
    Podcast = 2,
}

/// <summary>An episode's audio on the server (§4.4).</summary>
[JsonConverter(typeof(TolerantEnumConverter<PodcastAudioState>))]
public enum PodcastAudioState : byte
{
    None = 0,
    Queued = 1,
    Downloading = 2,
    Stored = 3,
    Failed = 4,
    Deferred = 5,
    Unknown = byte.MaxValue,
}

/// <summary>Why a download failed (§4.4).</summary>
[JsonConverter(typeof(TolerantEnumConverter<PodcastAudioFailureKind>))]
public enum PodcastAudioFailureKind : byte
{
    NotAudio = 1,
    TooLarge = 2,
    HttpError = 3,
    Timeout = 4,
    Refused = 5,
    Gone = 6,
    Unknown = byte.MaxValue,
}

/// <summary>Why a download waits (§4.4).</summary>
[JsonConverter(typeof(TolerantEnumConverter<PodcastAudioDeferredReason>))]
public enum PodcastAudioDeferredReason : byte
{
    AudioQuota = 1,
    DiskHeadroom = 2,
    Unknown = byte.MaxValue,
}

/// <summary>Who asked for a download (§4.4).</summary>
[JsonConverter(typeof(TolerantEnumConverter<PodcastAudioRequestedBy>))]
public enum PodcastAudioRequestedBy : byte
{
    User = 1,
    Auto = 2,
    Unknown = byte.MaxValue,
}

/// <summary>The iTunes episode type (§4.3).</summary>
[JsonConverter(typeof(TolerantEnumConverter<PodcastEpisodeType>))]
public enum PodcastEpisodeType : byte
{
    Full = 0,
    Trailer = 1,
    Bonus = 2,
    Unknown = byte.MaxValue,
}

/// <summary>Where a chapter came from (§4.5); a higher-precedence source wins on read.</summary>
[JsonConverter(typeof(TolerantEnumConverter<PodcastChapterSource>))]
public enum PodcastChapterSource : byte
{
    Feed = 1,
    Json = 2,
    AudioFile = 3,
    Unknown = byte.MaxValue,
}

/// <summary>The iTunes podcast type (§4.2).</summary>
[JsonConverter(typeof(TolerantEnumConverter<PodcastType>))]
public enum PodcastType : byte
{
    Episodic = 1,
    Serial = 2,
    Unknown = byte.MaxValue,
}

/// <summary>An artwork rendition (§5.4 #11).</summary>
public enum PodcastArtworkSize : byte
{
    Small = 2,
    Medium = 3,
    Original = 1,
}

/// <summary>The episode list's sort (§5.2 #6).</summary>
public enum PodcastEpisodeSortBy
{
    Newest = 0,
    Oldest = 1,
}
