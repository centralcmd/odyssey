using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Internet.News.Fetching;
using Odyssey.Core.Internet.News.Jobs;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Podcasts;

/// <summary>
/// The chapter-file phase of the news job (issue #556 §3.4): up to <see cref="PodcastTiming.ChaptersPerTick"/>
/// Podcasting 2.0 chapter files per tick per process, each claimed by the episode row's chapter lease, fetched through
/// the news egress (≤ 1 MiB) and read with <c>MaxDepth = 32</c>. Every value passes <see cref="PodcastFields.NormalizeChapters"/>.
/// </summary>
public sealed class PodcastChaptersPhase(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    NewsHostThrottleGate throttle,
    TimeProvider time,
    ILogger<PodcastChaptersPhase> logger)
{
    private const int MaxJsonDepth = 32;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<int> RunAsync(Guid owner, CancellationToken cancellationToken)
    {
        var fetched = 0;
        foreach (var id in await ClaimAsync(owner, cancellationToken))
        {
            fetched += await ProcessAsync(id, owner, cancellationToken) ? 1 : 0;
        }

        return fetched;
    }

    public static Task<bool> HasWorkAsync(OdysseyContext context, DateTime now, CancellationToken cancellationToken) =>
        context.PodcastEpisodes.AnyAsync(
            e => e.ChaptersFetchState == PodcastChaptersFetchState.Pending && (e.ChaptersNextAttemptAtUtc == null || e.ChaptersNextAttemptAtUtc <= now),
            cancellationToken);

    /// <summary>
    /// Reads a Podcasting 2.0 chapters document (§6.3): <c>{"version":"1.x","chapters":[{"startTime":…,"endTime":…,
    /// "title":…,"url":…}]}</c>. Anything malformed, too deep or of another version yields no chapters, never an exception.
    /// </summary>
    public static IReadOnlyList<PodcastNormalizedChapter> ParseChapters(ReadOnlySpan<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = MaxJsonDepth, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String
                || version.GetString() is not { } v || !v.StartsWith('1')
                || !root.TryGetProperty("chapters", out var chapters) || chapters.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var raw = new List<PodcastRawChapter>();
            foreach (var chapter in chapters.EnumerateArray().Take(PodcastLimits.MaxChapters * 2))
            {
                if (chapter.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                raw.Add(new PodcastRawChapter(
                    PodcastFields.MillisecondsFromSeconds(Number(chapter, "startTime")),
                    PodcastFields.MillisecondsFromSeconds(Number(chapter, "endTime")),
                    Text(chapter, "title"),
                    Text(chapter, "url")));
            }

            return PodcastFields.NormalizeChapters(raw);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<Guid>> ClaimAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = Now;
        var due = await (from episode in context.PodcastEpisodes.AsNoTracking()
                         join feed in context.NewsFeeds.AsNoTracking() on episode.NewsFeedId equals feed.NewsFeedId
                         where feed.Kind == NewsFeedKind.Podcast && feed.DeletionRequestedAtUtc == null
                             && episode.ChaptersFetchState == PodcastChaptersFetchState.Pending
                             && (episode.ChaptersNextAttemptAtUtc == null || episode.ChaptersNextAttemptAtUtc <= now)
                             && (episode.ChaptersLeaseOwner == null || episode.ChaptersLeaseExpiresAtUtc < now)
                         orderby episode.ChaptersNextAttemptAtUtc, episode.NewsArticleId
                         select episode.NewsArticleId)
            .Take(PodcastTiming.ChaptersPerTick)
            .ToListAsync(cancellationToken);

        var claimed = new List<Guid>(due.Count);
        var expires = now + PodcastTiming.PhaseLease;
        foreach (var id in due)
        {
            var leased = context.Database.IsRelational()
                ? await context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE `PodcastEpisodes` SET `ChaptersLeaseOwner` = {owner}, `ChaptersLeaseExpiresAtUtc` = {expires} WHERE `NewsArticleId` = {id} AND (`ChaptersLeaseOwner` IS NULL OR `ChaptersLeaseExpiresAtUtc` < {now})",
                    cancellationToken) == 1
                : await LeaseInMemoryAsync(context, id, owner, now, expires, cancellationToken);
            if (leased)
            {
                claimed.Add(id);
            }
        }

        return claimed;
    }

    private static async Task<bool> LeaseInMemoryAsync(
        OdysseyContext context, Guid id, Guid owner, DateTime now, DateTime expires, CancellationToken cancellationToken)
    {
        var row = await context.PodcastEpisodes.FirstOrDefaultAsync(e => e.NewsArticleId == id, cancellationToken);
        if (row is null || (row.ChaptersLeaseOwner is not null && row.ChaptersLeaseExpiresAtUtc >= now))
        {
            return false;
        }

        row.ChaptersLeaseOwner = owner;
        row.ChaptersLeaseExpiresAtUtc = expires;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> ProcessAsync(Guid id, Guid owner, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<OdysseyContext>();
        var episode = await context.PodcastEpisodes.FirstOrDefaultAsync(e => e.NewsArticleId == id && e.ChaptersLeaseOwner == owner, cancellationToken);
        var feedUrl = episode is null ? null : await context.NewsFeeds.AsNoTracking()
            .Where(f => f.NewsFeedId == episode.NewsFeedId).Select(f => f.Url).FirstOrDefaultAsync(cancellationToken);
        if (episode is null || feedUrl is null || !Uri.TryCreate(feedUrl, UriKind.Absolute, out var feedUri)
            || NewsUrlKey.CanonicalHost(feedUri) is not { } feedHost)
        {
            return false;
        }

        if (episode.ChaptersUrl is null || !Uri.TryCreate(episode.ChaptersUrl, UriKind.Absolute, out var source))
        {
            episode.ChaptersFetchState = PodcastChaptersFetchState.None;
            Release(episode);
            await context.SaveChangesAsync(cancellationToken);
            return false;
        }

        var grants = await services.GetRequiredService<INewsSettingsLookup>().GetAllowListAsync(cancellationToken);
        var http = await NewsHttpFetch.GetAsync(
            httpClients.CreateClient(NewsEgress.HttpClientName),
            source,
            new NewsEgressContext(feedHost, NewsEgressPurpose.Image, grants),
            etag: null,
            lastModified: null,
            PodcastTiming.MaxChapterFileBytes,
            async (host, ct) => await throttle.TryTakeAsync(host, ct) is null,
            takeFirstHost: true,
            NewsEgress.ImageTimeout,
            cancellationToken);
        var now = Now;
        if (http.Outcome == NewsHttpOutcome.Throttled)
        {
            Release(episode);
            await context.SaveChangesAsync(cancellationToken);
            return false;
        }

        if (http.Outcome != NewsHttpOutcome.Body)
        {
            var permanent = http.ErrorKind is NewsFeedErrorKind.TooLarge or NewsFeedErrorKind.BlockedAddress;
            Retry(episode, now, permanent);
            await context.SaveChangesAsync(cancellationToken);
            return false;
        }

        var chapters = ParseChapters(http.Body);
        await PodcastChapterRows.ReplaceInPlaceAsync(context, episode.NewsArticleId, PodcastChapterSource.Json, chapters, cancellationToken);
        episode.ChaptersFetchState = PodcastChaptersFetchState.Fetched;
        episode.ChaptersAttempts = 0;
        episode.ChaptersNextAttemptAtUtc = null;
        Release(episode);
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Episode {EpisodeId}: {Count} chapters read from its chapter file.", episode.NewsArticleId, chapters.Count);
        return true;
    }

    private static void Retry(PodcastEpisode episode, DateTime now, bool permanent)
    {
        episode.ChaptersAttempts = (byte)Math.Min(byte.MaxValue, episode.ChaptersAttempts + 1);
        if (!permanent && episode.ChaptersAttempts <= PodcastTiming.Backoff.Length)
        {
            episode.ChaptersNextAttemptAtUtc = now + PodcastTiming.Backoff[episode.ChaptersAttempts - 1];
        }
        else
        {
            episode.ChaptersFetchState = PodcastChaptersFetchState.Failed;
            episode.ChaptersNextAttemptAtUtc = null;
        }

        Release(episode);
    }

    private static void Release(PodcastEpisode episode)
    {
        episode.ChaptersLeaseOwner = null;
        episode.ChaptersLeaseExpiresAtUtc = null;
    }

    private static double? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
