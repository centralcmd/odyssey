using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.ApiClient.Resources;

/// <summary>The episode list's filters (issue #556 §5.2 #6); the cursor binds to every one of them.</summary>
public sealed record PodcastEpisodeListFilter
{
    public IReadOnlyCollection<Guid>? FeedIds { get; init; }

    public IReadOnlyCollection<PodcastAudioState>? AudioStates { get; init; }

    public string? Search { get; init; }

    public bool IncludeArchivedFeeds { get; init; }

    /// <summary><c>true</c> saved to a device, <c>false</c> never saved, <c>null</c> both.</summary>
    public bool? SavedToDevice { get; init; }

    public PodcastEpisodeSortBy SortBy { get; init; } = PodcastEpisodeSortBy.Newest;
}

/// <summary>
/// The podcast endpoints (issue #556 §5.1–§5.4): the podcast feed catalogue, episodes, the server download and its
/// removal, and the content URLs the browser loads itself. Feed writes stay on <see cref="INewsApiClient"/>.
/// </summary>
public interface IPodcastsApiClient
{
    /// <summary>The feed catalogue narrowed to podcast feeds (<c>GET /api/news-feeds/catalogue?kind=Podcast</c>).</summary>
    Task<ApiResult<List<NewsFeedCatalogueEntry>>> GetCatalogueAsync(CancellationToken ct = default);

    /// <summary>
    /// One page of podcast feeds in every state (<c>GET /api/news-feeds?kind=Podcast&amp;state=All</c>), each with its
    /// <c>podcast</c> block (episode, saved and stored counts) and its tags — what the Podcasts page's header sums.
    /// </summary>
    Task<ApiResult<PagedResult<ExistingNewsFeed>>> ListFeedsAsync(int page, int pageSize, CancellationToken ct = default);

    Task<ApiResult<PodcastEpisodePage>> ListEpisodesAsync(PodcastEpisodeListFilter filter, int limit, string? cursor = null, CancellationToken ct = default);

    Task<ApiResult<PodcastEpisodeDetail>> GetEpisodeAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Queues a server download. <c>202</c> when queued or already in flight, <c>200</c> when already stored; a
    /// <c>429 podcast-audio-cooldown</c> carries <c>Retry-After</c>, and a full store is <c>507 podcast-audio-quota-reached</c>.
    /// </summary>
    Task<ApiResult<PodcastEpisodeAudioInfo>> RequestAudioAsync(Guid id, CancellationToken ct = default);

    /// <summary>Removes stored audio or cancels a pending download; <c>409 podcast-audio-busy</c> while it downloads.</summary>
    Task<ApiResult> RemoveAudioAsync(Guid id, CancellationToken ct = default);

    /// <summary>The audio content URL, loaded by the browser as a download (it carries the session cookie itself).</summary>
    string AudioUrl(Guid episodeId);

    /// <summary>
    /// The in-browser playback URL (<c>GET …/audio/stream</c>, issue #593), resolved against the same API base as
    /// <see cref="AudioUrl"/>. Inline and ranged; it never counts as saved to a device.
    /// </summary>
    string AudioStreamUrl(Guid episodeId);

    /// <summary>
    /// The same-origin path pattern every <see cref="AudioStreamUrl"/> matches, so the player script can refuse any
    /// other source without restating the route.
    /// </summary>
    string AudioStreamPathPattern { get; }

    /// <summary>
    /// Saves the shared playback position, or (<paramref name="completed"/>) marks the listen finished
    /// (<c>PUT …/playback</c>). <c>204</c>; <c>409 podcast-audio-not-stored</c> for a finish without stored audio.
    /// </summary>
    Task<ApiResult> SavePlaybackAsync(Guid id, int? positionSeconds, bool completed, CancellationToken ct = default);

    string ArtworkUrl(Guid artworkId, PodcastArtworkSize size = PodcastArtworkSize.Medium);
}

/// <inheritdoc cref="IPodcastsApiClient" />
public sealed class PodcastsApiClient(IOdysseyApi api) : IPodcastsApiClient
{
    private const string Episodes = "api/podcast-episodes";
    private const string Artwork = "api/podcast-artwork";

    public Task<ApiResult<List<NewsFeedCatalogueEntry>>> GetCatalogueAsync(CancellationToken ct = default) =>
        api.GetAsync<List<NewsFeedCatalogueEntry>>($"api/news-feeds/catalogue?kind={NewsFeedKindFilter.Podcast}", ct);

    public Task<ApiResult<PagedResult<ExistingNewsFeed>>> ListFeedsAsync(int page, int pageSize, CancellationToken ct = default) =>
        api.GetPagedAsync<ExistingNewsFeed>(PagedQuery.For("api/news-feeds")
            .Window(page, pageSize)
            .Add("state", NewsFeedState.All.ToString())
            .Add("kind", NewsFeedKindFilter.Podcast.ToString())
            .Build(), ct);

    public Task<ApiResult<PodcastEpisodePage>> ListEpisodesAsync(PodcastEpisodeListFilter filter, int limit, string? cursor = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        // Window(1, limit) keeps offset at 0, which the server accepts beside a cursor.
        var query = PagedQuery.For(Episodes)
            .Window(1, limit)
            .AddMany("feedIds", filter.FeedIds?.Select(id => id.ToString()))
            .AddMany("audioState", filter.AudioStates?.Select(s => s.ToString()))
            .Add("search", filter.Search)
            .AddBool("includeArchivedFeeds", filter.IncludeArchivedFeeds ? true : null)
            .AddBool("savedToDevice", filter.SavedToDevice)
            .Add("sortBy", filter.SortBy.ToString())
            .Add("cursor", cursor);
        return api.GetAsync<PodcastEpisodePage>(query.Build(), ct);
    }

    public Task<ApiResult<PodcastEpisodeDetail>> GetEpisodeAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<PodcastEpisodeDetail>($"{Episodes}/{id}", ct);

    public Task<ApiResult<PodcastEpisodeAudioInfo>> RequestAudioAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync<PodcastEpisodeAudioInfo>(HttpMethod.Post, $"{Episodes}/{id}/audio", null, ct);

    public Task<ApiResult> RemoveAudioAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Episodes}/{id}/audio", null, ct);

    public string AudioUrl(Guid episodeId) => new Uri(api.BaseAddress!, $"{Episodes}/{episodeId}/audio/content").ToString();

    public string AudioStreamUrl(Guid episodeId) => new Uri(api.BaseAddress!, $"{Episodes}/{episodeId}/audio/stream").ToString();

    public string AudioStreamPathPattern =>
        $"^{System.Text.RegularExpressions.Regex.Escape(new Uri(api.BaseAddress!, Episodes).AbsolutePath)}/[0-9a-fA-F]{{8}}-[0-9a-fA-F]{{4}}-[0-9a-fA-F]{{4}}-[0-9a-fA-F]{{4}}-[0-9a-fA-F]{{12}}/audio/stream$";

    public Task<ApiResult> SavePlaybackAsync(Guid id, int? positionSeconds, bool completed, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Episodes}/{id}/playback", new PodcastPlaybackUpdate { PositionSeconds = positionSeconds, Completed = completed }, ct);

    public string ArtworkUrl(Guid artworkId, PodcastArtworkSize size = PodcastArtworkSize.Medium) =>
        new Uri(api.BaseAddress!, $"{Artwork}/{artworkId}/content?size={size}").ToString();
}
