namespace Odyssey.Core.Recipes.Import;

/// <summary>The recipe-import queue's compile-time timing (issue #602 §3), the statement queue's shape.</summary>
public static class RecipeImportJobTiming
{
    /// <summary>The worker tick.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);

    /// <summary>A Queued import no worker claimed within this ends <c>Failed / not_started</c>.</summary>
    public static readonly TimeSpan UnclaimedTimeout = TimeSpan.FromMinutes(15);

    /// <summary>How many of the oldest Queued rows one tick tries to claim.</summary>
    public const int ClaimCandidates = 5;

    /// <summary>The upper bound of the per-attempt transport timeout (the file-analysis timeout setting).</summary>
    public const int MaxTimeoutSeconds = 140;

    /// <summary>Two attempts at most (A16): the one transport retry on 429, 503 or 529.</summary>
    public static TimeSpan TotalRequestTimeout(int timeoutSeconds) => TimeSpan.FromSeconds(timeoutSeconds) * 2;

    /// <summary>How long a claimed import may stay Running before a sweep fails it <c>interrupted</c>.</summary>
    public static TimeSpan Lease(int timeoutSeconds) =>
        TotalRequestTimeout(timeoutSeconds) * 3 / 2 + TimeSpan.FromMinutes(2);

    /// <summary>The retention job's manual-request poll; a scheduled pass runs hourly.</summary>
    public static readonly TimeSpan RetentionPoll = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan RetentionTick = TimeSpan.FromHours(1);

    /// <summary>Imports purged per retention chunk.</summary>
    public const int RetentionChunk = 50;
}
