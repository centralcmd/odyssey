using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Jobs;

/// <summary>What a claim took: the feed's settings and the fetch-state values the fetch starts from.</summary>
public sealed record NewsClaim
{
    public required Guid FeedId { get; init; }

    public required string Url { get; init; }

    public required Guid Owner { get; init; }

    public required Guid Fence { get; init; }

    public required DateTime ClaimTime { get; init; }

    /// <summary>The effective interval at claim time (the feed's, else the default, lengthened to the feed's TTL).</summary>
    public required TimeSpan Interval { get; init; }

    public bool DownloadImages { get; init; }

    /// <summary>The feed was not archived at claim time: new articles' <c>FeedVisible</c>.</summary>
    public bool FeedVisible { get; init; }

    public string? ETag { get; init; }

    public string? LastModified { get; init; }

    public byte[]? LastBodySha256 { get; init; }

    /// <summary>A URL change is pending: the validators are neither sent nor trusted on this fetch.</summary>
    public bool ValidatorsResetPending { get; init; }

    public int ConsecutiveFailures { get; init; }

    public string? Language { get; init; }

    public string? SuggestedUrl { get; init; }

    /// <summary>The feed's articles today when it was claimed (the day already rolled).</summary>
    public int ArticlesToday { get; init; }

    /// <summary>What the feed carries at claim time (issue #556 §3.4).</summary>
    public NewsFeedKind Kind { get; init; }

    /// <summary>The podcast's auto-download switch at claim time.</summary>
    public bool AutoDownload { get; init; }

    /// <summary>When the feed's type last changed: a podcast baseline at or before it does not count (§3.4 step 7).</summary>
    public DateTime? KindChangedAtUtc { get; init; }
}

/// <summary>How a fenced write ended.</summary>
public enum NewsFenceOutcome
{
    Ok = 0,

    /// <summary>The fetch-state row no longer carries this fetch's lease and fence: another writer took over.</summary>
    LeaseLost = 1,

    /// <summary>The feed's deletion was requested: the fetch aborts and writes nothing more.</summary>
    DeletionRequested = 2,
}

/// <summary>
/// One fetch's hold on its feed (issue #470 §3.3, "Fence mechanics"). The fetch keeps the feed's current
/// <c>FetchFence</c>; every fenced write — a lease renewal, an item-batch save, the completion — goes through
/// <see cref="WithFenceAsync"/>, one at a time (a single async lock per fetch, so a renewal and a batch never run at
/// once). Each re-reads the fetch-state row in the caller's context, checks that the lease and fence are still this
/// fetch's, reads <c>NewsFeeds.DeletionRequestedAtUtc</c>, and rotates the fence, so the save's <c>WHERE</c> carries
/// the old one: a lease overrun can never double-ingest.
/// </summary>
public sealed class NewsFetchSession(NewsClaim claim) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Guid fence = claim.Fence;

    public NewsClaim Claim { get; } = claim;

    public Guid FeedId => Claim.FeedId;

    /// <summary>Set once a fenced write found the lease lost or the deletion requested; nothing more is written.</summary>
    public NewsFenceOutcome? Aborted { get; private set; }

    /// <summary>
    /// Runs <paramref name="body"/> with the fetch-state row loaded, checked and its fence rotated. The body makes the
    /// save(s); a concurrency failure on the fetch-state row is the lease lost. With
    /// <paramref name="allowPendingDeletion"/> the deletion check is skipped — the abort's own final save that clears
    /// the lease so the purge can start straight away.
    /// </summary>
    public async Task<NewsFenceOutcome> WithFenceAsync(
        OdysseyContext context,
        Func<NewsFeedFetchState, Task> body,
        CancellationToken cancellationToken,
        bool allowPendingDeletion = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Aborted == NewsFenceOutcome.LeaseLost)
            {
                return NewsFenceOutcome.LeaseLost;
            }

            var state = await context.NewsFeedFetchStates.FirstOrDefaultAsync(s => s.NewsFeedId == FeedId, cancellationToken);
            if (state is null || state.LeaseOwner != Claim.Owner || state.FetchFence != fence)
            {
                Aborted = NewsFenceOutcome.LeaseLost;
                return NewsFenceOutcome.LeaseLost;
            }

            if (!allowPendingDeletion)
            {
                var deletion = await context.NewsFeeds.AsNoTracking()
                    .Where(f => f.NewsFeedId == FeedId)
                    .Select(f => new { f.DeletionRequestedAtUtc })
                    .FirstOrDefaultAsync(cancellationToken);
                if (deletion is null || deletion.DeletionRequestedAtUtc is not null)
                {
                    Aborted ??= NewsFenceOutcome.DeletionRequested;
                    return NewsFenceOutcome.DeletionRequested;
                }
            }

            // Marking the token modified is what puts the OLD value in the UPDATE's WHERE clause.
            var next = Guid.NewGuid();
            state.FetchFence = next;
            try
            {
                await body(state);
            }
            catch (DbUpdateConcurrencyException ex) when (ex.Entries.Any(e => e.Entity is NewsFeedFetchState))
            {
                Aborted = NewsFenceOutcome.LeaseLost;
                return NewsFenceOutcome.LeaseLost;
            }

            fence = next;
            return NewsFenceOutcome.Ok;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The fence a successful write left, for a test.</summary>
    public Guid CurrentFence => fence;

    public void Dispose() => gate.Dispose();
}

/// <summary>Why an attempt ended, which decides how the completion schedules the feed (§3.4 step 6, §9.3).</summary>
public enum NewsFetchResultKind
{
    /// <summary>Fetched and parsed; items ingested (possibly with transient skips).</summary>
    Success = 0,

    /// <summary>A <c>304</c>, or a body identical to the last one.</summary>
    NotModified = 1,

    /// <summary>A handled failure: back-off.</summary>
    Failure = 2,

    /// <summary>A redirect hop (or the first request) found its host busy: <c>now + 1 min</c>, not a failure.</summary>
    Throttled = 3,

    /// <summary>The lease was lost or the deletion requested: nothing more is written.</summary>
    Aborted = 4,

    /// <summary>The host is stopping: the lease is cleared and the crash count put back.</summary>
    Stopped = 5,
}

/// <summary>What one fetch attempt produced, for the completion.</summary>
public sealed record NewsFetchResult
{
    public required NewsFetchResultKind Kind { get; init; }

    public NewsFeedErrorKind ErrorKind { get; init; }

    public int? HttpStatus { get; init; }

    /// <summary>Server-composed, naming a host only.</summary>
    public string? ErrorMessage { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    public string? ETag { get; init; }

    public string? LastModified { get; init; }

    public byte[]? BodySha256 { get; init; }

    public string? SuggestedUrl { get; init; }

    public bool RedirectedFromHttps { get; init; }

    public Parsing.NewsParsedFeed? Feed { get; init; }

    /// <summary>A transient skip happened (a cap, the text quota, the headroom, a swept reservation, a lock wait, the budget): the validators are cleared.</summary>
    public bool TransientSkip { get; init; }

    /// <summary>A store refusal was among the transient skips: the job holds <c>StorageUnavailable</c> this tick.</summary>
    public bool StorageRefused { get; init; }

    /// <summary>Items that failed deterministically (retried on at most the next 3 full parses).</summary>
    public int ItemFailures { get; init; }

    public int ItemsStored { get; init; }

    public int ItemsRevised { get; init; }
}
