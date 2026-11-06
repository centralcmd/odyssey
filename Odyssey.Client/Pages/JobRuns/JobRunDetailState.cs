using Odyssey.Dtos.Application;

namespace Odyssey.Client.Pages.JobRuns;

public enum JobRunDetailStatus
{
    Loading,
    Loaded,

    /// <summary><c>404</c>: the run has been pruned.</summary>
    Gone,

    /// <summary>Network or <c>5xx</c>.</summary>
    Failed,
}

/// <summary>
/// One run's detail as the page caches it (issue #410 §3.6, F4): fetched on first expand; a finished run's is kept, a
/// Running or open-Held one's is dropped on every Refresh and replaced on each of its follow ticks.
/// </summary>
public sealed record JobRunDetailState(JobRunDetailStatus Status, JobRunDetail? Value = null)
{
    public static readonly JobRunDetailState Loading = new(JobRunDetailStatus.Loading);

    /// <summary>A detail that can still change, so a Refresh re-reads it.</summary>
    public bool IsLive => Value is { } detail
        && (detail.Status == JobRunStatus.Running || (detail.Status == JobRunStatus.Held && detail.FinishedAtUtc is null));
}
