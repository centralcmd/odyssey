using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Jobs;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Internet;

namespace Odyssey.Core.Internet.News.Podcasts;

/// <summary>
/// The audio retention job (issue #556 §3.7), catalogued as <see cref="JobType.PodcastAudioRetention"/>. It polls every
/// <see cref="Poll"/> for a manual request and runs a scheduled pass once <see cref="PodcastTiming.RetentionTick"/> has
/// passed since this process's last one. A pass releases, with the deletion grace, the stored audio of every podcast
/// feed with auto-delete on whose episode was downloaded to a device since this copy was stored and whose countdown —
/// from the later of that download and the rule's last change — has run out. Audio never downloaded is never
/// auto-deleted. Each row is a fenced save in chunks of <see cref="PodcastTiming.ReleaseChunk"/>; a row someone else is
/// touching loses the fence and is taken next pass.
/// </summary>
public sealed class PodcastAudioRetentionJob(
    IServiceScopeFactory scopes,
    INewsCounterWriter counters,
    TimeProvider time,
    ILogger<PodcastAudioRetentionJob> logger) : IPeriodicJob
{
    /// <summary>The manual-request poll; a scheduled pass runs far less often.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromMinutes(1);

    private DateTime? lastPass;

    public string Name => "podcast-audio-retention";

    public TimeSpan Interval => Poll;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        JobRunRecorder recorder;
        await using (var scope = scopes.CreateAsyncScope())
        {
            recorder = scope.ServiceProvider.GetRequiredService<JobRunRecorder>();
        }

        await recorder.HeartbeatAsync(JobType.PodcastAudioRetention, cancellationToken);
        var manualRunId = await recorder.ClaimManualAsync(JobType.PodcastAudioRetention, cancellationToken);
        var scheduled = Now - lastPass is not { } since || since >= PodcastTiming.RetentionTick;
        if (manualRunId is null && !scheduled)
        {
            return;
        }

        var (released, bytes, failure) = (0, 0L, (JobFailureCategory?)null);
        try
        {
            (released, bytes) = await PassAsync(cancellationToken);
            lastPass = Now;
        }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException ? JobFailureCategory.Cancelled : JobFailureCategory.Internal;
            throw;
        }
        finally
        {
            void Add(PodcastAudioRetentionJobRun run)
            {
                run.AudioReleased += released;
                run.AudioBytesReleased += bytes;
            }

            if (manualRunId is { } runId)
            {
                await recorder.FinishAsync<PodcastAudioRetentionJobRun>(JobType.PodcastAudioRetention, runId, Add,
                    failure is { } category ? (JobRunStatus.Failed, category) : (JobRunStatus.Succeeded, null), CancellationToken.None);
            }
            else if (released > 0)
            {
                await recorder.ExtendBurstAsync<PodcastAudioRetentionJobRun>(JobType.PodcastAudioRetention, Add, CancellationToken.None);
                await recorder.CloseBurstAsync(JobType.PodcastAudioRetention, CancellationToken.None);
            }
        }
    }

    /// <summary>Any eligible row (the catalogue's <c>HasWork</c>).</summary>
    public async Task<bool> HasWorkAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await Eligible(scope.ServiceProvider.GetRequiredService<OdysseyContext>(), Now).AnyAsync(cancellationToken);
    }

    /// <summary>One pass: every eligible row, a chunk at a time. Returns the rows and bytes released.</summary>
    public async Task<(int Released, long Bytes)> PassAsync(CancellationToken cancellationToken)
    {
        var released = 0;
        long bytes = 0;
        var skipped = new HashSet<Guid>();
        while (true)
        {
            List<Guid> chunk;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
                chunk = await Eligible(context, Now).AsNoTracking()
                    .Where(a => !skipped.Contains(a.NewsArticleId))
                    .OrderBy(a => a.NewsArticleId).Select(a => a.NewsArticleId)
                    .Take(PodcastTiming.ReleaseChunk).ToListAsync(cancellationToken);
            }

            if (chunk.Count == 0)
            {
                break;
            }

            foreach (var id in chunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var freed = await ReleaseAsync(id, cancellationToken);
                if (freed is { } size)
                {
                    released++;
                    bytes += size;
                    logger.LogInformation("Podcast audio {EpisodeId} was auto-deleted; its file is removed after the deletion grace.", id);
                }
                else
                {
                    skipped.Add(id);
                }
            }
        }

        if (released > 0)
        {
            logger.LogInformation("Podcast audio retention released {Count} file(s), {Bytes} bytes.", released, bytes);
        }

        return (released, bytes);
    }

    /// <summary>The §3.7 step 1 predicate.</summary>
    /// <remarks>Counts from the consumed-at time (<see cref="PodcastConsumption"/>, issue #593): the later of the device
    /// download and the in-browser listen made after the current copy was stored.</remarks>
    public static IQueryable<PodcastEpisodeAudio> Eligible(OdysseyContext context, DateTime now) =>
        from row in PodcastConsumption.Rows(context.PodcastEpisodeAudio, context.PodcastEpisodes)
        join feed in context.NewsFeeds on row.Audio.NewsFeedId equals feed.NewsFeedId
        where row.Audio.State == PodcastAudioState.Stored
            && feed.Kind == NewsFeedKind.Podcast && feed.ArchivedAtUtc == null && feed.DeletionRequestedAtUtc == null
            && feed.AutoDeleteEnabled
            && row.ConsumedAtUtc != null
            && (feed.AutoDeleteChangedAtUtc == null || feed.AutoDeleteChangedAtUtc <= row.ConsumedAtUtc
                ? row.ConsumedAtUtc!.Value.AddDays(feed.AutoDeleteAfterDays) <= now
                : feed.AutoDeleteChangedAtUtc!.Value.AddDays(feed.AutoDeleteAfterDays) <= now)
        select row.Audio;

    private async Task<long?> ReleaseAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var row = await Eligible(context, Now).FirstOrDefaultAsync(a => a.NewsArticleId == id, cancellationToken);
        if (row is null)
        {
            return null;
        }

        try
        {
            return await NewsTransactions.RunAsync(context, async () =>
            {
                var size = await PodcastAudioRelease.StageAsync(store, row, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
                await counters.AddToPodcastStorageAsync(context, PodcastStorageDelta.AudioReleased(size), cancellationToken);
                return (long?)size;
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
    }
}
