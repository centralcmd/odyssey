using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Dtos.Application;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.Thumbnails;

/// <summary>
/// The thumbnail job's rows in the shared run log (issue #409 §3.2, §3.3, §3.5): bursts, held periods, the run log's
/// heartbeat and the manual run's one-time re-queue. Part of <see cref="PhotoThumbnailJobScenarios"/>, so it runs on
/// EF InMemory and on real MariaDB alike.
/// </summary>
public abstract partial class PhotoThumbnailJobScenarios
{
    /// <summary>AC 4, 31: a working tick opens a burst carrying this process's WorkerId; an idle tick closes it.</summary>
    [SkippableFact]
    public async Task A_working_tick_opens_a_burst_and_an_idle_tick_closes_it()
    {
        var host = await NewThumbnailHostAsync();
        await AddPhotoAsync(host, ThumbnailFixtures.Jpeg(400, 300));

        await Job(host).RunOnceAsync(default);

        var open = (PhotoThumbnailJobRun)Assert.Single(await RunLogAsync(host));
        Assert.Equal((JobRunStatus.Running, 1), (open.Status, open.PhotosRendered));
        var heartbeat = Assert.Single(await WithContextAsync(host, c => c.JobHeartbeats.AsNoTracking().ToListAsync()));
        Assert.Equal((JobType.PhotoThumbnails, open.WorkerId!.Value), (heartbeat.JobType, heartbeat.WorkerId));

        Clock.Advance(PhotoThumbnailTiming.Interval);
        await Job(host).RunOnceAsync(default);

        var closed = Assert.Single(await RunLogAsync(host));
        Assert.Equal((JobRunStatus.Succeeded, 1), (closed.Status, closed.ItemsProcessed));
    }

    /// <summary>AC 7: a held worker writes one Held row with its reason, and records no burst.</summary>
    [SkippableFact]
    public async Task A_policy_failed_worker_records_one_held_period()
    {
        SelfTest.Behaviour = () => new ThumbnailSelfTestResult("probe");
        var host = await NewThumbnailHostAsync();
        await AddPhotoAsync(host, ThumbnailFixtures.Jpeg(400, 300));

        await Job(host).RunOnceAsync(default);
        Clock.Advance(PhotoThumbnailTiming.Interval);
        await Job(host).RunOnceAsync(default);

        var held = Assert.Single(await RunLogAsync(host));
        Assert.Equal((JobRunStatus.Held, JobHeldReason.DecoderPolicyFailed), (held.Status, held.HeldReason));
        Assert.Null(held.FinishedAtUtc);
    }

    /// <summary>
    /// AC 17, 35: a manual run re-queues each Failed job never re-queued by hand — attempts reset, ManualRequeues 1 —
    /// skips one already re-queued, and never touches an Unsupported job (a crash included).
    /// </summary>
    [SkippableFact]
    public async Task A_manual_run_re_queues_each_failed_job_once_and_never_an_unsupported_one()
    {
        var host = await NewThumbnailHostAsync();
        var failed = await AddPhotoAsync(host, ThumbnailFixtures.Jpeg(400, 300));
        var requeuedBefore = await AddPhotoAsync(host, ThumbnailFixtures.Jpeg(400, 300), fileName: "b.jpg");
        var crashed = await AddPhotoAsync(host, ThumbnailFixtures.Jpeg(400, 300), fileName: "c.jpg");
        await WithContextAsync(host, async context =>
        {
            foreach (var job in await context.PhotoThumbnailJobs.ToListAsync())
            {
                job.State = job.PhotoId == crashed ? PhotoThumbnailJobState.Unsupported : PhotoThumbnailJobState.Failed;
                job.FailureCode = job.PhotoId == crashed ? PhotoThumbnailFailure.Crash : PhotoThumbnailFailure.Exhausted;
                job.Attempts = PhotoThumbnailTiming.MaxCountedAttempts;
                job.AttemptedRecipeVersion = PhotoThumbnailRecipe.Version;
                job.ManualRequeues = (byte)(job.PhotoId == requeuedBefore ? 1 : 0);
            }

            await context.SaveChangesAsync();
        });
        var runId = await RequestManualRunAsync(host);

        await Job(host).RunOnceAsync(default);

        var requeued = await JobRowAsync(host, failed);
        Assert.Equal((PhotoThumbnailJobState.Pending, 0, (byte)1), (requeued.State, requeued.Attempts, requeued.ManualRequeues));
        Assert.Equal(PhotoThumbnailJobState.Failed, (await JobRowAsync(host, requeuedBefore)).State);
        var unsupported = await JobRowAsync(host, crashed);
        Assert.Equal((PhotoThumbnailJobState.Unsupported, (byte)0), (unsupported.State, unsupported.ManualRequeues));

        var manual = (PhotoThumbnailJobRun)(await RunLogAsync(host)).Single(r => r.JobRunId == runId);
        Assert.Equal((JobRunStatus.Succeeded, 1), (manual.Status, manual.PhotosRequeued));
        Assert.NotNull(manual.ClaimedAtUtc);

        // A second manual run finds the re-queued job already spent its one manual re-queue once it fails again.
        await WithContextAsync(host, async context =>
        {
            var job = await context.PhotoThumbnailJobs.SingleAsync(j => j.PhotoId == failed);
            job.State = PhotoThumbnailJobState.Failed;
            await context.SaveChangesAsync();
        });
        Clock.Advance(TimeSpan.FromMinutes(2));
        var second = await RequestManualRunAsync(host);
        await Job(host).RunOnceAsync(default);

        Assert.Equal(PhotoThumbnailJobState.Failed, (await JobRowAsync(host, failed)).State);
        Assert.Equal(0, ((PhotoThumbnailJobRun)(await RunLogAsync(host)).Single(r => r.JobRunId == second)).PhotosRequeued);
    }

    /// <summary>
    /// Architect review on PR #418: a manual run cut short by shutdown is recorded Failed / Cancelled, as the weather
    /// and file-storage runs record it — never Failed / Internal, which would file a deploy restart as an error.
    /// </summary>
    [SkippableFact]
    public async Task A_manual_run_cut_short_by_shutdown_is_recorded_cancelled()
    {
        var host = await NewThumbnailHostAsync();
        await AddPhotoAsync(host, ThumbnailFixtures.Jpeg(400, 300));
        var runId = await RequestManualRunAsync(host);
        using var stopping = new CancellationTokenSource();
        Renderer.Behaviour = _ =>
        {
            stopping.Cancel();
            throw new OperationCanceledException(stopping.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Job(host).RunOnceAsync(stopping.Token));

        var manual = (await RunLogAsync(host)).Single(r => r.JobRunId == runId);
        Assert.Equal((JobRunStatus.Failed, JobFailureCategory.Cancelled), (manual.Status, manual.FailureCategory));
    }

    private static Task<List<JobRun>> RunLogAsync(FileStorageTestHost host) =>
        WithContextAsync(host, c => c.JobRuns.AsNoTracking().OrderBy(r => r.StartedAtUtc).ToListAsync());

    private Task<Guid> RequestManualRunAsync(FileStorageTestHost host) => WithContextAsync(host, async context =>
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var run = new PhotoThumbnailJobRun
        {
            JobRunId = Guid.NewGuid(), Trigger = JobRunTrigger.Manual, Status = JobRunStatus.Running,
            StartedAtUtc = now, LastActivityAtUtc = now,
        };
        context.JobRuns.Add(run);
        await context.SaveChangesAsync();
        return run.JobRunId;
    });
}
