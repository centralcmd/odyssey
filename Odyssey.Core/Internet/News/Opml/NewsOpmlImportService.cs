using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Odyssey.Context;
using Odyssey.Core.Internet.News.Content;
using Odyssey.Core.Internet.News.Reading;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Opml;

/// <summary>
/// <c>POST /api/news-feeds/opml</c> (issue #508 §3, §8): one step with a report. The file is read as a stream and each
/// distinct feed's outcome is decided in document order as it is read — <c>Invalid</c>, <c>PendingDeletion</c> /
/// <c>AlreadySubscribed</c>, <c>FeedLimit</c>, else <c>Created</c> — so what is kept per feed is bounded independently
/// of the file (§10): a URL key and a position for every feed, full details only for the feeds that can still be
/// created and for the first <see cref="NewsOpmlLimits.MaxOtherReportRows"/> other rows. Then the created feeds' tags
/// are resolved in their own committed saves, and the feeds are written in ONE transaction that re-counts the feeds and
/// cuts the batch to the room left. The import makes no outbound request.
/// </summary>
public sealed class NewsOpmlImportService(
    OdysseyContext context,
    INewsSettingsLookup settings,
    NewsTagService tags,
    TimeProvider time,
    ILogger<NewsOpmlImportService> logger)
{
    public async Task<NewsOpmlImportResult> ImportAsync(Stream file, bool canCreateTags, string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var resolved = await settings.GetAsync(cancellationToken);
        var grants = await settings.GetAllowListAsync(cancellationToken);

        // One query for every existing key (≤ NewsMaxFeeds plus the feeds pending deletion).
        var existingRows = await NewsQueryGuard.RunAsync(() => context.NewsFeeds.AsNoTracking().News()
            .Select(f => new { f.NewsFeedId, f.UrlKeyHash, f.DeletionRequestedAtUtc })
            .ToListAsync(cancellationToken));
        var existing = new Dictionary<UrlKeyHash, (Guid Id, bool Pending)>();
        foreach (var row in existingRows)
        {
            if (row.UrlKeyHash.Length == NewsLimits.HashLength)
            {
                existing[UrlKeyHash.From(row.UrlKeyHash)] = (row.NewsFeedId, row.DeletionRequestedAtUtc is not null);
            }
        }

        var room = Math.Max(0, resolved.MaxFeeds - existingRows.Count(r => r.DeletionRequestedAtUtc is null));
        var state = new ReadState(resolved.OpmlMaxImportEntries, room, existing, grants);
        try
        {
            OpmlDocumentReader.Read(file, state.Add, cancellationToken);
        }
        catch (OpmlUnreadableException)
        {
            throw NewsErrors.OpmlUnreadable();
        }

        // Tags, before any feed transaction: every lookup and insert commits on its own (§8.3.5, B1).
        var names = state.Candidates.SelectMany(c => c.TagNames).Distinct(StringComparer.Ordinal).ToList();
        var resolution = names.Count == 0
            ? new NewsTagResolution(new Dictionary<string, Guid>(), [], [])
            : await tags.ResolveOrCreateAsync(names, canCreateTags, cancellationToken);

        var (kept, cut) = await SaveFeedsAsync(state.Candidates, resolution, resolved.MaxFeeds, cancellationToken);
        var result = Report(state, resolution, kept, cut);
        logger.LogInformation(
            "News OPML import by {ActorUserId}: {Feeds} feeds ({MergedOutlines} merged outlines), {Created} created, "
            + "{AlreadySubscribed} already subscribed, {PendingDeletion} pending deletion, {Invalid} invalid, "
            + "{FeedLimit} over the feed limit, {TagsCreated} tags created.",
            userId, result.Feeds, result.MergedOutlines, result.Created, result.AlreadySubscribed, result.PendingDeletion,
            result.Invalid, result.FeedLimit, result.TagsCreated.Count);
        return result;
    }

    /// <summary>
    /// The feed transaction (§3 step 7): the count re-taken and the batch cut to the room left, then each feed with its
    /// fetch-state row, its counter row and its tag links — the rows <see cref="NewsFeedService.CreateAsync"/> writes — in
    /// one save. A <c>UrlKeyHash</c> collision with a concurrent create is <c>409 news-opml-conflict</c>, with nothing
    /// committed; an interrupted transaction is <c>503 news-busy</c> (<see cref="NewsQueryGuard.IsBusy"/>).
    /// </summary>
    private async Task<(List<(Candidate Candidate, Guid FeedId, List<string> Tags)> Kept, List<Candidate> Cut)> SaveFeedsAsync(
        IReadOnlyList<Candidate> candidates, NewsTagResolution resolution, int maxFeeds, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return ([], []);
        }

        try
        {
            return await NewsQueryGuard.RunAsync(() => NewsTransactions.RunAsync(context, async () =>
            {
                context.ChangeTracker.Clear();
                var count = await context.NewsFeeds.News().CountAsync(f => f.DeletionRequestedAtUtc == null, cancellationToken);
                var room = Math.Max(0, maxFeeds - count);
                var kept = new List<(Candidate, Guid, List<string>)>();
                var now = time.GetUtcNow().UtcDateTime;
                foreach (var candidate in candidates.Take(room))
                {
                    var feed = new NewsFeed
                    {
                        NewsFeedId = Guid.NewGuid(),
                        Url = candidate.Url,
                        UrlKeyHash = candidate.UrlKeyHash,
                        CustomTitle = candidate.CustomTitle,
                        FetchIntervalMinutes = candidate.FetchIntervalMinutes,
                        DownloadImages = candidate.DownloadImages,
                        IsActive = candidate.IsActive,
                        CreatedAtUtc = now,
                        SettingsVersion = Guid.NewGuid(),
                    };
                    context.NewsFeeds.Add(feed);
                    context.NewsFeedFetchStates.Add(new NewsFeedFetchState
                    {
                        NewsFeedId = feed.NewsFeedId,
                        FetchFence = Guid.NewGuid(),
                        NextFetchAtUtc = now,
                    });
                    context.NewsFeedCounters.Add(new NewsFeedCounter { NewsFeedId = feed.NewsFeedId });

                    // Two spellings the database treats as one tag (Cafe, Café) resolve to one id: one link.
                    var linked = new List<string>();
                    var tagIds = new HashSet<Guid>();
                    foreach (var name in candidate.TagNames)
                    {
                        if (resolution.Resolved.TryGetValue(name, out var tagId) && tagIds.Add(tagId))
                        {
                            linked.Add(name);
                            context.NewsFeedTags.Add(new NewsFeedTag { NewsFeedTagId = Guid.NewGuid(), NewsFeedId = feed.NewsFeedId, NewsTagId = tagId });
                        }
                    }

                    kept.Add((candidate, feed.NewsFeedId, linked));
                }

                await context.SaveChangesAsync(cancellationToken);
                context.ChangeTracker.Clear();
                return (kept, candidates.Skip(room).ToList());
            }, cancellationToken));
        }
        catch (DbUpdateException ex) when (ex.GetBaseException() is MySqlException
        {
            ErrorCode: MySqlErrorCode.DuplicateKeyEntry or MySqlErrorCode.NoReferencedRow2 or MySqlErrorCode.NoReferencedRow,
        })
        {
            // A concurrent create took one of these URLs (or a concurrent delete removed a resolved tag). Nothing from
            // this import was committed; a retry re-evaluates and reports that feed AlreadySubscribed.
            context.ChangeTracker.Clear();
            throw NewsErrors.OpmlConflict();
        }
        catch
        {
            context.ChangeTracker.Clear();
            throw;
        }
    }

    private static NewsOpmlImportResult Report(
        ReadState state, NewsTagResolution resolution, List<(Candidate Candidate, Guid FeedId, List<string> Tags)> kept, List<Candidate> cut)
    {
        var createdRows = kept.Select(k => new NewsOpmlImportItem
        {
            Position = k.Candidate.Position,
            Url = k.Candidate.ReportUrl,
            Title = k.Candidate.ReportTitle,
            Outcome = NewsOpmlImportOutcome.Created,
            FeedId = k.FeedId,
            Tags = k.Tags,
            TagsDropped = k.Candidate.TagsDropped,
        });

        // The in-transaction cut turns created candidates into FeedLimit rows; they join the other rows in document
        // order. The first MaxOtherReportRows of all other rows are always among these (the rest came later).
        var others = state.OtherRows
            .Concat(cut.Select(c => new NewsOpmlImportItem
            {
                Position = c.Position,
                Url = c.ReportUrl,
                Title = c.ReportTitle,
                Outcome = NewsOpmlImportOutcome.FeedLimit,
            }))
            .OrderBy(r => r.Position)
            .Take(NewsOpmlLimits.MaxOtherReportRows)
            .ToList();
        var otherTotal = state.OtherTotal + cut.Count;

        var skipped = resolution.Skipped
            .DistinctBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        return new NewsOpmlImportResult
        {
            Feeds = state.Feeds,
            MergedOutlines = state.MergedOutlines,
            Created = kept.Count,
            AlreadySubscribed = state.AlreadySubscribed,
            PendingDeletion = state.PendingDeletion,
            Invalid = state.Invalid,
            FeedLimit = state.FeedLimit + cut.Count,
            TagsCreated = [.. resolution.Created],
            TagsSkipped = [.. skipped.Take(NewsOpmlLimits.MaxSkippedTagRows).Select(s => new NewsOpmlSkippedTag { Name = s.Name, Reason = s.Reason })],
            TagsSkippedTotal = skipped.Count,
            Items = [.. createdRows, .. others],
            ItemsTruncated = others.Count < otherTotal,
        };
    }

    /// <summary>A SHA-256 URL key as four words: 32 bytes per distinct feed, never a 64-character string.</summary>
    private readonly record struct UrlKeyHash(long A, long B, long C, long D)
    {
        public static UrlKeyHash From(ReadOnlySpan<byte> hash)
        {
            var words = MemoryMarshal.Cast<byte, long>(hash);
            return new UrlKeyHash(words[0], words[1], words[2], words[3]);
        }
    }

    /// <summary>A feed that can still be created: its fields and up to 32 distinct tag names in first-seen order.</summary>
    private sealed class Candidate(int position, NewsFeedAddress address, OpmlFeedOutline outline)
    {
        private readonly HashSet<string> keys = new(StringComparer.Ordinal);

        public int Position { get; } = position;

        public string Url { get; } = address.Url;

        public byte[] UrlKeyHash { get; } = address.UrlKeyHash;

        public string ReportUrl { get; } = ReportUrlOf(outline.Url);

        public string? ReportTitle { get; } = outline.ReportTitle;

        public string? CustomTitle { get; } = outline.CustomTitle;

        public int? FetchIntervalMinutes { get; } = outline.FetchIntervalMinutes;

        public bool DownloadImages { get; } = outline.DownloadImages;

        public bool IsActive { get; } = outline.IsActive;

        public List<string> TagNames { get; } = [];

        public int TagsDropped { get; private set; }

        /// <summary>§8.3.2 and §8.3.4: de-duplicated by <see cref="NewsTagService.NameKey"/> over the merged outlines.</summary>
        public void AddTags(IReadOnlyList<string> names)
        {
            foreach (var raw in names)
            {
                // A name over the length cap is kept only as far as it takes to report it NameTooLong.
                var name = raw.Length > NewsLimits.MaxTagNameLength ? NewsText.Clamp(raw, NewsLimits.MaxTagNameLength + 1) : raw;
                if (keys.Contains(NewsTagService.NameKey(name)))
                {
                    continue;
                }

                if (TagNames.Count >= NewsLimits.MaxTagIdsPerRequest)
                {
                    TagsDropped++;
                    continue;
                }

                keys.Add(NewsTagService.NameKey(name));
                TagNames.Add(name);
            }
        }
    }

    private static string ReportUrlOf(string url) => NewsText.Clamp(url, NewsOpmlLimits.MaxReportUrlLength);

    /// <summary>What the read keeps: bounded by the entry cap, the room and the report row cap, never by the file.</summary>
    private sealed class ReadState(
        int maxEntries,
        int room,
        IReadOnlyDictionary<UrlKeyHash, (Guid Id, bool Pending)> existing,
        IReadOnlyList<NewsPrivateHostGrant> grants)
    {
        private readonly Dictionary<UrlKeyHash, int> positions = [];
        private readonly Dictionary<int, Candidate> candidatesByPosition = [];

        public List<Candidate> Candidates { get; } = [];

        public List<NewsOpmlImportItem> OtherRows { get; } = [];

        public int OtherTotal { get; private set; }

        public int Feeds { get; private set; }

        public int MergedOutlines { get; private set; }

        public int AlreadySubscribed { get; private set; }

        public int PendingDeletion { get; private set; }

        public int Invalid { get; private set; }

        public int FeedLimit { get; private set; }

        public void Add(OpmlFeedOutline outline)
        {
            UrlKeyHash? key = NewsUrlKey.UrlKey(outline.Url) is { } urlKey ? UrlKeyHash.From(NewsUrlKey.Hash(urlKey)) : null;
            if (key is { } known && positions.TryGetValue(known, out var earlier))
            {
                // The first outline supplied the feed; a later one only adds tag names, and only to a created feed.
                MergedOutlines++;
                if (candidatesByPosition.TryGetValue(earlier, out var merged))
                {
                    merged.AddTags(outline.TagNames);
                }

                return;
            }

            if (++Feeds > maxEntries)
            {
                throw NewsErrors.OpmlTooManyEntries(maxEntries);
            }

            var position = Feeds;
            if (key is { } fresh)
            {
                positions[fresh] = position;
            }

            if (!NewsFeedUrlRules.TryValidate(outline.Url, grants, out var address, out var message))
            {
                Invalid++;
                Other(position, outline, NewsOpmlImportOutcome.Invalid, null, message);
                return;
            }

            if (existing.TryGetValue(key!.Value, out var match))
            {
                if (match.Pending)
                {
                    PendingDeletion++;
                    Other(position, outline, NewsOpmlImportOutcome.PendingDeletion, null, null);
                }
                else
                {
                    AlreadySubscribed++;
                    Other(position, outline, NewsOpmlImportOutcome.AlreadySubscribed, match.Id, null);
                }

                return;
            }

            if (Candidates.Count >= room)
            {
                FeedLimit++;
                Other(position, outline, NewsOpmlImportOutcome.FeedLimit, null, null);
                return;
            }

            var candidate = new Candidate(position, address!, outline);
            candidate.AddTags(outline.TagNames);
            Candidates.Add(candidate);
            candidatesByPosition[position] = candidate;
        }

        private void Other(int position, OpmlFeedOutline outline, NewsOpmlImportOutcome outcome, Guid? feedId, string? message)
        {
            OtherTotal++;
            if (OtherRows.Count < NewsOpmlLimits.MaxOtherReportRows)
            {
                OtherRows.Add(new NewsOpmlImportItem
                {
                    Position = position,
                    Url = ReportUrlOf(outline.Url),
                    Title = outline.ReportTitle,
                    Outcome = outcome,
                    FeedId = feedId,
                    Message = message,
                });
            }
        }
    }
}
