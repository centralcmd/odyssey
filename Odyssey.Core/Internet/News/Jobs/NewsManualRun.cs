using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Jobs;
using Odyssey.Dtos.Application;

namespace Odyssey.Core.Internet.News.Jobs;

/// <summary>
/// The news job's manual run (issue #470 §5.5 item 27), in one place for the worker that claims it and for the API's
/// inline execution under <c>Testing</c>: every eligible feed — active, not archived, not pending deletion, not
/// quarantined, last attempted at least <see cref="NewsJobTiming.ManualRunFloor"/> ago — is marked due by the conditional
/// refresh write (§3.3), in chunks. The ordinary ticks then fetch them under their per-feed leases.
/// </summary>
public static class NewsManualRun
{
    public const int Chunk = 100;

    /// <summary>Marks the eligible feeds due; returns how many were marked (<c>FeedsMarked</c>).</summary>
    public static async Task<int> MarkDueAsync(OdysseyContext context, INewsFetchStateFlags flags, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flags);
        var cutoff = now - NewsJobTiming.ManualRunFloor;
        var eligible = await (from state in context.NewsFeedFetchStates.AsNoTracking()
                              join feed in context.NewsFeeds.AsNoTracking() on state.NewsFeedId equals feed.NewsFeedId
                              where feed.IsActive && feed.ArchivedAtUtc == null && feed.DeletionRequestedAtUtc == null
                                  && state.QuarantinedAtUtc == null
                                  && (state.LastFetchAttemptAtUtc == null || state.LastFetchAttemptAtUtc <= cutoff)
                              orderby state.NewsFeedId
                              select state.NewsFeedId).ToListAsync(cancellationToken);
        var marked = 0;
        foreach (var chunk in eligible.Chunk(Chunk))
        {
            foreach (var feedId in chunk)
            {
                marked += await flags.RequestRefreshAsync(context, feedId, now, cancellationToken) ? 1 : 0;
            }

            context.ChangeTracker.Clear();
        }

        return marked;
    }

    /// <summary>
    /// Claims the oldest unclaimed manual request of the news job, marks the eligible feeds and finishes the run
    /// <c>Succeeded</c> with <c>FeedsMarked</c> — what a worker tick does with it. Returns the run it finished, if any.
    /// </summary>
    public static async Task<Guid?> ExecuteClaimedAsync(
        JobRunRecorder recorder, OdysseyContext context, INewsFetchStateFlags flags, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        if (await recorder.ClaimManualAsync(JobType.NewsFeedFetch, cancellationToken) is not { } runId)
        {
            return null;
        }

        var marked = await MarkDueAsync(context, flags, now, cancellationToken);
        await recorder.FinishAsync<NewsFeedJobRun>(JobType.NewsFeedFetch, runId, run => run.FeedsMarked = marked,
            (JobRunStatus.Succeeded, null), CancellationToken.None);
        return runId;
    }
}
