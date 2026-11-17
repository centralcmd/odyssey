using Odyssey.Context;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Jobs;

/// <summary>The timing constants of the news job (issue #470 §3.3, §3.4, §6.2, §9.3).</summary>
public static class NewsJobTiming
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    /// <summary>A fetch's lease; renewed every <see cref="LeaseRenewal"/> while the deadline has not passed.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(3);

    public static readonly TimeSpan LeaseRenewal = TimeSpan.FromSeconds(60);

    /// <summary>The whole fetch, claim through ingest: 30 s for the network and 60 s for ingest.</summary>
    public static readonly TimeSpan FetchDeadline = TimeSpan.FromSeconds(90);

    public static readonly TimeSpan IngestBudget = TimeSpan.FromSeconds(60);

    /// <summary>The per-item deadline (§3.4 step 5), applied to the HTML walk.</summary>
    public static readonly TimeSpan ItemDeadline = TimeSpan.FromSeconds(2);

    /// <summary>A claim sets the next fetch at least this far out, provisionally, so a crash cannot hammer a publisher.</summary>
    public static readonly TimeSpan ProvisionalMinimum = TimeSpan.FromHours(1);

    /// <summary>A refresh is due only once the last attempt is at least this old (§3.3).</summary>
    public static readonly TimeSpan RefreshFloor = TimeSpan.FromSeconds(60);

    /// <summary>The next attempt after a throttled redirect hop, a graceful stop or a lost host race.</summary>
    public static readonly TimeSpan ShortRetry = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(24);

    /// <summary>Concurrent fetches per process (§3.4 step 3).</summary>
    public const int FetchSlots = 4;

    /// <summary>The purge's budget per transaction and per tick (§3.4 step 1).</summary>
    public static readonly TimeSpan PurgeChunkBudget = TimeSpan.FromMilliseconds(250);

    public static readonly TimeSpan PurgeTickBudget = TimeSpan.FromSeconds(5);

    /// <summary>The image phase (§3.4 step 8).</summary>
    public const int ImagesPerTick = 50;

    public static readonly TimeSpan ImagePhaseBudget = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan ImageLease = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan Maintenance = TimeSpan.FromHours(1);

    /// <summary>At startup, overdue feeds are spread over this window (§9.3).</summary>
    public static readonly TimeSpan StartupSpread = TimeSpan.FromMinutes(10);

    /// <summary>The manual run marks feeds whose last attempt is at least this old (§5.5 item 27).</summary>
    public static readonly TimeSpan ManualRunFloor = TimeSpan.FromMinutes(15);

    /// <summary>The crash breaker (§3.4 step 7): this many crash events within <see cref="BreakerWindow"/> hold the job for the window.</summary>
    public const int BreakerEvents = 3;

    public static readonly TimeSpan BreakerWindow = TimeSpan.FromHours(1);

    /// <summary>A feed is quarantined at this many counted crash events.</summary>
    public const int QuarantineAt = 3;

    /// <summary>An edit within this long of the last one overwrites without a new revision (§4.9).</summary>
    public static readonly TimeSpan RevisionWindow = TimeSpan.FromHours(1);

    /// <summary>An unchanged article's <c>LastSeenAtUtc</c> is touched at most this often.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    /// <summary>A future publication date is capped this far after first sight (§4.7, AC 15).</summary>
    public static readonly TimeSpan SortCap = TimeSpan.FromHours(1);

    /// <summary>Deterministic item failures are retried on at most this many further full parses (§3.4 step 6).</summary>
    public const int ItemFailureReparses = 3;

    /// <summary>The image retry ladder (§6.2): then <c>Failed</c>.</summary>
    public static readonly IReadOnlyList<TimeSpan> ImageBackoff =
        [TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(24), TimeSpan.FromHours(72)];

    /// <summary>A store-quota deferral is retried this often (§6.3).</summary>
    public static readonly TimeSpan StoreQuotaRetry = TimeSpan.FromHours(1);

    /// <summary>Image repair (§3.4 step 9).</summary>
    public const int RepairProbePerPass = 200;

    public const int RepairRequeuesPerHour = 100;

    public static readonly TimeSpan RepairInterval = TimeSpan.FromHours(24);

    public static readonly TimeSpan ThrottleRetention = TimeSpan.FromHours(24);

    public static readonly TimeSpan CrashEventRetention = TimeSpan.FromDays(7);

    /// <summary>The run log's stale window: a live burst is never older than a fetch's lease plus a tick.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
}

/// <summary>The pure scheduling and quota rules of the news job.</summary>
public static class NewsFetchRules
{
    /// <summary>
    /// A feed's effective interval: its own, else the <c>NewsDefaultFetchIntervalMinutes</c> setting, lengthened to the
    /// publisher's TTL when that asks for less frequent polling (never past the per-feed maximum).
    /// </summary>
    public static TimeSpan EffectiveInterval(int? feedInterval, int defaultInterval, int? feedTtlMinutes)
    {
        var minutes = feedInterval ?? defaultInterval;
        if (feedTtlMinutes is { } ttl && ttl > minutes)
        {
            minutes = Math.Min(ttl, NewsLimits.MaxFetchIntervalMinutes);
        }

        return TimeSpan.FromMinutes(Math.Clamp(minutes, NewsLimits.MinFetchIntervalMinutes, NewsLimits.MaxFetchIntervalMinutes));
    }

    /// <summary>§9.3: <c>min(interval × 2^min(failures, 6), 24 h)</c> ±10 %, from the failure count AFTER this failure.</summary>
    public static TimeSpan Backoff(TimeSpan interval, int failures, double jitter)
    {
        var factor = Math.Pow(2, Math.Clamp(failures, 0, 6));
        var raw = TimeSpan.FromTicks((long)Math.Min(interval.Ticks * factor, NewsJobTiming.MaxBackoff.Ticks));
        return TimeSpan.FromTicks((long)(raw.Ticks * (1 + (Math.Clamp(jitter, -1, 1) * 0.1))));
    }

    /// <summary>A <c>Retry-After</c> is honoured, clamped to [interval, 24 h].</summary>
    public static TimeSpan RetryAfter(TimeSpan requested, TimeSpan interval) =>
        requested < interval ? interval : requested > NewsJobTiming.MaxBackoff ? NewsJobTiming.MaxBackoff : requested;

    /// <summary><c>min(coalesce(Published, FirstSeen), FirstSeen + 1 h)</c>, fixed at insert (§4.7, AC 15).</summary>
    public static DateTime SortAt(DateTime? publishedAtUtc, DateTime firstSeenAtUtc)
    {
        var published = publishedAtUtc ?? firstSeenAtUtc;
        var cap = firstSeenAtUtc + NewsJobTiming.SortCap;
        return published < cap ? published : cap;
    }

    /// <summary>
    /// Recomputes <c>QuotaReachedAtUtc</c>/<c>QuotaReason</c> (§6.3): <c>TextQuota</c> while the text quota is reached,
    /// else <c>DailyCap</c> while the article cap is, else neither. The timestamp keeps its first value while the
    /// reason stays the same and is reset when it changes. Image caps never set it. Returns whether anything changed.
    /// </summary>
    public static bool ApplyQuotaReason(NewsFeedFetchState state, bool textQuotaReached, bool dailyCapReached, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(state);
        NewsFeedQuotaReason? reason = textQuotaReached ? NewsFeedQuotaReason.TextQuota
            : dailyCapReached ? NewsFeedQuotaReason.DailyCap
            : null;
        if (reason is null)
        {
            if (state.QuotaReason is null && state.QuotaReachedAtUtc is null)
            {
                return false;
            }

            state.QuotaReason = null;
            state.QuotaReachedAtUtc = null;
            return true;
        }

        if (state.QuotaReason == reason && state.QuotaReachedAtUtc is not null)
        {
            return false;
        }

        state.QuotaReason = reason;
        state.QuotaReachedAtUtc = now;
        return true;
    }

    /// <summary>Moves the per-day counters to <paramref name="today"/> when the stored day is another one.</summary>
    public static void RollDay(NewsFeedFetchState state, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.DailyQuotaDate != today)
        {
            state.DailyQuotaDate = today;
            state.ArticlesToday = 0;
            state.ImageBytesToday = 0;
        }
    }

    /// <summary>The next UTC midnight after <paramref name="now"/>: when a daily image cap lifts.</summary>
    public static DateTime NextUtcMidnight(DateTime now) => now.Date.AddDays(1);

    /// <summary>The image retry after a failed attempt, or <c>null</c> once the ladder is exhausted (then <c>Failed</c>).</summary>
    public static TimeSpan? ImageRetry(int attemptsSoFar) =>
        attemptsSoFar >= 1 && attemptsSoFar <= NewsJobTiming.ImageBackoff.Count ? NewsJobTiming.ImageBackoff[attemptsSoFar - 1] : null;
}
