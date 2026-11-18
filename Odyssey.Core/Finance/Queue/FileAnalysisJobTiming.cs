namespace Odyssey.Core.Finance;

/// <summary>
/// The queue's timing, compile-time by product decision (issue #444 §8): none of these is a policy an
/// administrator would tune, and each is derived from or bounded by something that is not editable at
/// runtime either.
/// </summary>
public static class FileAnalysisJobTiming
{
    /// <summary>The worker tick. Queue latency for an idle worker is at most one tick plus a claim.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);

    /// <summary>A Queued job no worker claimed within this ends <c>Failed / not_started</c>.</summary>
    public static readonly TimeSpan UnclaimedTimeout = TimeSpan.FromMinutes(15);

    /// <summary>How many of the oldest Queued rows one tick tries to claim before giving up for the tick.</summary>
    public const int ClaimCandidates = 5;

    /// <summary>
    /// The analysis ceiling (maintainer decision, issue #444 v4). The upload cap is admin-editable up to the
    /// 1 GiB transport ceiling, so it cannot bound the worker's memory; this does — one 32 MiB blob plus its
    /// base64 copy under the worker's 1 GiB <c>mem_limit</c>, with one job per process.
    /// </summary>
    public const long MaxAnalysisFileBytes = 32L * 1024 * 1024;

    public const int MinTimeoutSeconds = 10;
    public const int MaxTimeoutSeconds = 140;

    /// <summary>
    /// The provider pipeline's whole budget for one run: the resilience handler's
    /// <c>TotalRequestTimeout</c>, two attempts' worth of <c>FileAnalysis:TimeoutSeconds</c>.
    /// </summary>
    public static TimeSpan TotalRequestTimeout(int timeoutSeconds) => TimeSpan.FromSeconds(timeoutSeconds) * 2;

    /// <summary>
    /// How long a claimed job may stay Running before any replica's sweep fails it <c>interrupted</c>:
    /// 1.5 × the provider budget plus two minutes for the blob load and the writes (8 min at the default
    /// 120 s). No renewal is needed — the run cannot outlive the budget it is bounded by.
    /// </summary>
    public static TimeSpan Lease(int timeoutSeconds) =>
        TotalRequestTimeout(timeoutSeconds) * 3 / 2 + TimeSpan.FromMinutes(2);
}
