using Odyssey.Client.Components;
using Odyssey.Dtos;
using Odyssey.Dtos.Internet;

namespace Odyssey.Client.Pages.Internet;

/// <summary>
/// The Podcasts page's download-status polling rules (issue #559 §3.2.5 "Polling"), kept apart from the page so the
/// cadence, back-off and fan-out are tested on their own. The page owns the timer and the request; this class only
/// answers "when next", "which podcasts" and "what did that failure mean".
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Every 10 s while any loaded episode is Queued or Downloading; every 60 s while only Waiting for space remains,
/// and after 30 minutes of that the loop stops (<see cref="WaitStopped"/>) until Check again.</item>
/// <item>One list request per tick for at most <see cref="MaxFeedsPerTick"/> podcasts, round robin in list order.</item>
/// <item>401/403 stop for the page's lifetime; 429/503 wait for <c>Retry-After</c> capped at 5 minutes; anything else
/// backs off 20 s → 40 s → 80 s → 160 s → 5 minutes. Three consecutive failures show the polling line.</item>
/// </list>
/// </remarks>
public sealed class PodcastAudioPoller
{
    public static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan WaitingInterval = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan WaitingLimit = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan FirstBackoff = TimeSpan.FromMilliseconds(20_000);

    /// <summary>Podcasts named in one tick's request (the API's filter cap).</summary>
    public const int MaxFeedsPerTick = NewsLimits.MaxFilterIds;

    /// <summary>In-flight rows read per tick (the list request's <c>limit</c>, its maximum page).</summary>
    public const int RowsPerTick = NewsLimits.MaxPageSize;

    /// <summary>Episodes that left the in-flight set re-read with the detail endpoint per tick.</summary>
    public const int DetailReadsPerTick = 10;

    /// <summary>Consecutive failures before each in-flight card says it couldn't refresh.</summary>
    public const int FailuresShown = 3;

    public static readonly IReadOnlyCollection<PodcastAudioState> PolledStates =
        [PodcastAudioState.Queued, PodcastAudioState.Downloading, PodcastAudioState.Deferred];

    private DateTimeOffset? waitingSince;
    private int nextFeedOffset;

    /// <summary>A 401 or 403 stopped polling for the page's lifetime.</summary>
    public bool Stopped { get; private set; }

    /// <summary>Only Waiting for space remained for 30 minutes: the loop stopped and the cards offer Check again.</summary>
    public bool WaitStopped { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    /// <summary>The polling line shows (and is announced once) after <see cref="FailuresShown"/> failures in a row.</summary>
    public bool Trouble => ConsecutiveFailures >= FailuresShown;

    /// <summary>
    /// The delay before the next tick, or <c>null</c> when nothing is polled (no in-flight episode, stopped, or the
    /// waiting loop ran out).
    /// </summary>
    public TimeSpan? NextDelay(IEnumerable<PodcastAudioState> loadedStates, DateTimeOffset now)
    {
        if (Stopped)
        {
            return null;
        }

        var states = loadedStates.Where(OdsPodcastAudioState.InFlight).ToList();
        if (states.Count == 0)
        {
            waitingSince = null;
            WaitStopped = false;
            return null;
        }

        if (states.Any(s => s is PodcastAudioState.Queued or PodcastAudioState.Downloading))
        {
            waitingSince = null;
            WaitStopped = false;
            return Backoff ?? ActiveInterval;
        }

        waitingSince ??= now;
        if (now - waitingSince >= WaitingLimit)
        {
            WaitStopped = true;
            return null;
        }

        return Backoff ?? WaitingInterval;
    }

    /// <summary>The wait a failure set, until the next success.</summary>
    public TimeSpan? Backoff { get; private set; }

    /// <summary>The podcasts this tick names: the next <see cref="MaxFeedsPerTick"/> in list order, round robin.</summary>
    public IReadOnlyList<Guid> FeedsForTick(IReadOnlyList<Guid> inFlightFeedsInListOrder)
    {
        ArgumentNullException.ThrowIfNull(inFlightFeedsInListOrder);
        var feeds = inFlightFeedsInListOrder.Distinct().ToList();
        if (feeds.Count <= MaxFeedsPerTick)
        {
            nextFeedOffset = 0;
            return feeds;
        }

        if (nextFeedOffset >= feeds.Count)
        {
            nextFeedOffset = 0;
        }

        var tick = feeds.Skip(nextFeedOffset).Take(MaxFeedsPerTick).ToList();
        nextFeedOffset += tick.Count;
        return tick;
    }

    public void Succeeded()
    {
        ConsecutiveFailures = 0;
        Backoff = null;
    }

    /// <summary>Records a failed tick. Returns true when this failure is the one that first shows the polling line.</summary>
    public bool Failed(int? status, TimeSpan? retryAfter)
    {
        if (status is 401 or 403)
        {
            Stopped = true;
            return false;
        }

        ConsecutiveFailures++;
        Backoff = status is 429 or 503 && retryAfter is { } wait
            ? (wait > MaxWait ? MaxWait : wait < TimeSpan.Zero ? FirstBackoff : wait)
            : Exponential(ConsecutiveFailures);
        return ConsecutiveFailures == FailuresShown;
    }

    /// <summary>Check again: forget the failures and the waiting clock, and poll at once.</summary>
    public void CheckAgain()
    {
        ConsecutiveFailures = 0;
        Backoff = null;
        WaitStopped = false;
        waitingSince = null;
    }

    private static TimeSpan Exponential(int failures)
    {
        var seconds = FirstBackoff.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 8));
        return seconds >= MaxWait.TotalSeconds ? MaxWait : TimeSpan.FromSeconds(seconds);
    }
}
