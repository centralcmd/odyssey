using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Odyssey.Context;
using Odyssey.Core;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Finance.ExchangeRateSync;
using Odyssey.Core.Jobs;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Core.Weather;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests.Jobs;

/// <summary>
/// The shared run log's recording half (issue #409 §3.1–§3.4, §3.6): the catalogue, bursts, held periods, the
/// <see cref="JobRun.Generation"/> token, the stale-run sweep and the retention prune.
/// </summary>
public sealed class JobRunRecordingTests : IAsyncDisposable
{
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
    private readonly OdysseyContext store = TestContextFactory.Create();
    private readonly ServiceProvider services;

    public JobRunRecordingTests()
    {
        services = new ServiceCollection().AddJobRunRecordingOver(store, clock).BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        await store.DisposeAsync();
    }

    private JobRunRecorder Recorder => services.GetRequiredService<JobRunRecorder>();

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private async Task<List<JobRun>> RunsAsync()
    {
        await using var context = NewContext();
        return await context.JobRuns.AsNoTracking().OrderBy(r => r.StartedAtUtc).ToListAsync();
    }

    private OdysseyContext NewContext() => services.CreateScope().ServiceProvider.GetRequiredService<OdysseyContext>();

    // ── The catalogue (§3.1, §4.1.1) ────────────────────────────────────────────────────────────────────

    /// <summary>AC 23: the copy sets <c>FailureCategory = ErrorKind</c>, which is only right while the ordinals match.</summary>
    [Fact]
    public void Failure_category_ordinals_equal_the_exchange_rate_sync_error_kinds()
    {
        foreach (var kind in Enum.GetValues<ExchangeRateSyncErrorKind>())
        {
            Assert.True(Enum.IsDefined((JobFailureCategory)(int)kind), $"{kind} has no failure category.");
        }

        Assert.Equal((int)ExchangeRateSyncErrorKind.ProviderUnavailable, (int)JobFailureCategory.ProviderUnavailable);
        Assert.Equal((int)ExchangeRateSyncErrorKind.ProviderRateLimited, (int)JobFailureCategory.ProviderRateLimited);
        Assert.Equal((int)ExchangeRateSyncErrorKind.ProviderMalformed, (int)JobFailureCategory.ProviderMalformed);
        Assert.Equal((int)ExchangeRateSyncErrorKind.UnsupportedCurrency, (int)JobFailureCategory.UnsupportedInput);
        Assert.Equal((int)ExchangeRateSyncErrorKind.SettingsUnusable, (int)JobFailureCategory.Configuration);
        Assert.Equal((int)ExchangeRateSyncErrorKind.TimeZoneUnavailable, (int)JobFailureCategory.Environment);
        Assert.Equal((int)ExchangeRateSyncErrorKind.Cancelled, (int)JobFailureCategory.Cancelled);
        Assert.Equal((int)ExchangeRateSyncErrorKind.Abandoned, (int)JobFailureCategory.Abandoned);
        Assert.Equal((int)ExchangeRateSyncErrorKind.Internal, (int)JobFailureCategory.Internal);
        Assert.Equal(10, (int)JobFailureCategory.Storage);
    }

    /// <summary>The run log's own ordinals are the sync's, so its rows copied across unchanged.</summary>
    [Fact]
    public void Status_and_trigger_ordinals_extend_the_exchange_rate_sync_ones()
    {
        foreach (var status in Enum.GetValues<ExchangeRateSyncRunStatus>())
        {
            Assert.Equal(status.ToString(), ((JobRunStatus)(int)status).ToString());
        }

        foreach (var trigger in Enum.GetValues<ExchangeRateSyncTrigger>())
        {
            Assert.Equal(trigger.ToString(), ((JobRunTrigger)(int)trigger).ToString());
        }

        Assert.Equal(5, (int)JobRunStatus.Held);
    }

    [Fact]
    public void The_catalogue_lists_each_job_once_with_its_derived_thresholds()
    {
        Assert.Equal(
            [JobType.ExchangeRateSync, JobType.Weather, JobType.FileStorageMaintenance, JobType.PhotoThumbnails, JobType.PhotoOriginalBackfill,
             JobType.FileBlobBackfill],
            JobCatalogue.Entries.Select(entry => entry.JobType));
        Assert.Equal(JobCatalogue.Entries.Count, JobCatalogue.Entries.Select(entry => entry.JobClass).Distinct().Count());
        Assert.All(JobCatalogue.Entries, entry => Assert.Equal(300, entry.NotTickingAfterSeconds));
        Assert.Equal(TimeSpan.FromHours(1), JobCatalogue.Get(JobType.FileStorageMaintenance).Cadence);
        Assert.True(JobCatalogue.Get(JobType.Weather).IsWorkDriven);
        Assert.True(JobCatalogue.Get(JobType.PhotoThumbnails).IsWorkDriven);
        Assert.True(JobCatalogue.Get(JobType.PhotoOriginalBackfill).IsWorkDriven);
        Assert.False(JobCatalogue.Get(JobType.ExchangeRateSync).IsWorkDriven);
        Assert.DoesNotContain(typeof(JobRunMaintenanceJob), JobCatalogue.Entries.Select(entry => entry.JobClass));
    }

    /// <summary>A14: only a catalogued member's exact NAME resolves — never a number, a composed list or the Unknown sentinel.</summary>
    [Theory]
    [InlineData("Weather", true)]
    [InlineData("photoThumbnails", true)]
    [InlineData("FILESTORAGEMAINTENANCE", true)]
    [InlineData("2", false)]
    [InlineData("Unknown", false)]
    [InlineData("ExchangeRateSync,Weather", false)]
    [InlineData(" Weather", false)]
    [InlineData("Nope", false)]
    [InlineData("", false)]
    public void A_route_segment_resolves_only_to_a_catalogued_job_name(string value, bool resolves)
    {
        Assert.Equal(resolves, JobCatalogue.TryResolve(value, out _));
    }

    /// <summary>
    /// Security review on PR #418: a direct caller that skips model validation still gets a validation error keyed
    /// on the date, never the conversion's ArgumentOutOfRangeException (a 500).
    /// </summary>
    [Theory]
    [InlineData(9999, 12, 31, true, "startedTo")]
    [InlineData(1, 1, 1, false, "startedFrom")]
    public void An_out_of_range_date_bound_is_a_validation_error(int year, int month, int day, bool nextDay, string field)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Kiritimati");

        var error = Assert.Throws<DomainValidationException>(
            () => BackgroundJobsService.BoundUtc(new DateOnly(year, month, day), zone, field, nextDay));

        Assert.Equal(400, error.StatusCode);
        Assert.True(error.Errors!.ContainsKey(field));
    }

    // ── Heartbeats ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_heartbeat_is_written_at_most_once_a_minute_per_job()
    {
        await Recorder.HeartbeatAsync(JobType.Weather, default);
        var first = Now;
        clock.Advance(TimeSpan.FromSeconds(30));
        await Recorder.HeartbeatAsync(JobType.Weather, default);

        await using (var context = NewContext())
        {
            var beat = Assert.Single(await context.JobHeartbeats.AsNoTracking().ToListAsync());
            Assert.Equal((JobType.Weather, Recorder.WorkerId, first), (beat.JobType, beat.WorkerId, beat.LastTickAtUtc));
        }

        clock.Advance(TimeSpan.FromSeconds(31));
        await Recorder.HeartbeatAsync(JobType.Weather, default);
        await using (var context = NewContext())
        {
            Assert.Equal(Now, (await context.JobHeartbeats.AsNoTracking().SingleAsync()).LastTickAtUtc);
        }
    }

    // ── Bursts (§3.2) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>AC 4: N working ticks write one row, an idle tick closes it, a burst over an hour continues in a second.</summary>
    [Fact]
    public async Task A_burst_is_one_row_closed_by_an_idle_tick_and_split_at_an_hour()
    {
        for (var i = 0; i < 3; i++)
        {
            await Recorder.ExtendBurstAsync<WeatherJobRun>(JobType.Weather, run => run.PointsAttempted += 2, default);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var open = Assert.Single(await RunsAsync());
        Assert.Equal((JobRunStatus.Running, JobRunTrigger.Scheduled, Recorder.WorkerId), (open.Status, open.Trigger, open.WorkerId));
        Assert.Equal(6, ((WeatherJobRun)open).PointsAttempted);
        Assert.Null(open.ItemsProcessed);

        await Recorder.CloseBurstAsync(JobType.Weather, default);
        var closed = (WeatherJobRun)Assert.Single(await RunsAsync());
        Assert.Equal(JobRunStatus.Succeeded, closed.Status);
        Assert.Equal(6, closed.ItemsProcessed);
        Assert.Equal(0, closed.ItemsFailed);
        Assert.Equal((long)TimeSpan.FromMinutes(3).TotalMilliseconds, closed.DurationMs);

        // A burst reaching an hour is finished and the work continues in a new row.
        await Recorder.ExtendBurstAsync<WeatherJobRun>(JobType.Weather, run => run.PointsAttempted++, default);
        clock.Advance(JobRunRecorder.MaxBurst);
        await Recorder.ExtendBurstAsync<WeatherJobRun>(JobType.Weather, run => run.PointsAttempted++, default);

        var runs = await RunsAsync();
        Assert.Equal(3, runs.Count);
        Assert.Equal(JobRunStatus.Succeeded, runs[1].Status);
        Assert.Equal(JobRunStatus.Running, runs[2].Status);
    }

    /// <summary>AC 5: three points attempted, one failing, is a partial success with the failure's category.</summary>
    [Fact]
    public void A_weather_burst_with_some_failures_is_partially_succeeded()
    {
        var run = new WeatherJobRun { PointsAttempted = 3, PointsFailed = 1, FailureKind = WeatherFetchFailureKind.RateLimited };
        Assert.Equal((JobRunStatus.PartiallySucceeded, JobFailureCategory.ProviderRateLimited), JobRunOutcomes.Of(run));

        run.PointsFailed = 3;
        Assert.Equal(JobRunStatus.Failed, JobRunOutcomes.Of(run).Status);
        Assert.Equal((JobRunStatus.Succeeded, null), JobRunOutcomes.Of(new WeatherJobRun { PointsAttempted = 2 }));
    }

    [Fact]
    public void A_thumbnail_burst_with_crashes_is_internal_and_with_bad_files_unsupported_input()
    {
        Assert.Equal((JobRunStatus.PartiallySucceeded, JobFailureCategory.Internal),
            JobRunOutcomes.Of(new PhotoThumbnailJobRun { PhotosRendered = 4, Crashes = 1 }));
        Assert.Equal((JobRunStatus.Failed, JobFailureCategory.UnsupportedInput),
            JobRunOutcomes.Of(new PhotoThumbnailJobRun { PhotosFailed = 2 }));
        Assert.Equal((JobRunStatus.Succeeded, null), JobRunOutcomes.Of(new PhotoThumbnailJobRun { PhotosRequeued = 3 }));
        Assert.Equal((JobRunStatus.PartiallySucceeded, JobFailureCategory.Storage),
            JobRunOutcomes.Of(new FileStorageMaintenanceJobRun { ObjectsScrubbed = 10, IntegrityFailures = 1 }));
    }

    [Fact]
    public void The_headline_counts_are_written_by_finish_from_each_subtype()
    {
        var sync = new ExchangeRateSyncJobRun { StartedAtUtc = Now, RowsInserted = 4, RowsDeleted = 2, RowsRejected = 1 };
        sync.Finish(JobRunStatus.Succeeded, null, Now);
        Assert.Equal((6, 1), (sync.ItemsProcessed, sync.ItemsFailed));

        var storage = new FileStorageMaintenanceJobRun
        {
            StartedAtUtc = Now, FilesErased = 1, AbandonedWritesSwept = 2, ObjectsScrubbed = 3, IntegrityFailures = 1,
        };
        storage.Finish(JobRunStatus.PartiallySucceeded, JobFailureCategory.Storage, Now);
        Assert.Equal((6, 1), (storage.ItemsProcessed, storage.ItemsFailed));

        var photos = new PhotoThumbnailJobRun { StartedAtUtc = Now, PhotosRendered = 5, PhotosFailed = 1, Crashes = 1, Hangs = 1 };
        photos.Finish(JobRunStatus.PartiallySucceeded, JobFailureCategory.Internal, Now);
        Assert.Equal((5, 3), (photos.ItemsProcessed, photos.ItemsFailed));

        var skipped = new WeatherJobRun { StartedAtUtc = Now };
        skipped.Finish(JobRunStatus.Skipped, JobFailureCategory.Internal, Now);
        Assert.Null(skipped.FailureCategory);
    }

    // ── Held periods (§3.3 step 3) ─────────────────────────────────────────────────────────────────────────

    /// <summary>AC 6/7: one held row for the whole period; a change of reason is a new period; release closes it.</summary>
    [Fact]
    public async Task A_held_period_is_one_row_per_reason_and_closes_on_the_first_free_tick()
    {
        await Recorder.HoldAsync<PhotoThumbnailJobRun>(JobType.PhotoThumbnails, JobHeldReason.BreakerTripped, null, default);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Recorder.HoldAsync<PhotoThumbnailJobRun>(JobType.PhotoThumbnails, JobHeldReason.BreakerTripped, null, default);

        var held = Assert.Single(await RunsAsync());
        Assert.Equal((JobRunStatus.Held, JobHeldReason.BreakerTripped, Recorder.WorkerId), (held.Status, held.HeldReason, held.WorkerId));
        Assert.Null(held.FinishedAtUtc);
        Assert.Null(held.FailureCategory);

        clock.Advance(TimeSpan.FromMinutes(1));
        await Recorder.HoldAsync<PhotoThumbnailJobRun>(JobType.PhotoThumbnails, JobHeldReason.DecoderPaused, null, default);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Recorder.ReleaseHoldAsync(JobType.PhotoThumbnails, default);

        var runs = await RunsAsync();
        Assert.Equal(2, runs.Count);
        Assert.All(runs, run => Assert.NotNull(run.FinishedAtUtc));
        Assert.Equal([JobHeldReason.BreakerTripped, JobHeldReason.DecoderPaused], runs.Select(run => run.HeldReason!.Value));
        Assert.All(runs, run => Assert.Equal(JobRunStatus.Held, run.Status));
    }

    /// <summary>AC 7: each held thumbnail state maps to its reason; a running worker is not held.</summary>
    [Theory]
    [InlineData(ThumbnailWorkerState.Paused, JobHeldReason.DecoderPaused)]
    [InlineData(ThumbnailWorkerState.Tripped, JobHeldReason.BreakerTripped)]
    [InlineData(ThumbnailWorkerState.PolicyFailed, JobHeldReason.DecoderPolicyFailed)]
    [InlineData(ThumbnailWorkerState.StorageUnavailable, JobHeldReason.StorageUnavailable)]
    public void Each_held_thumbnail_state_has_its_held_reason(ThumbnailWorkerState state, JobHeldReason reason)
    {
        Assert.Equal(reason, PhotoThumbnailJob.HeldReasonOf(state));
        Assert.Null(PhotoThumbnailJob.HeldReasonOf(ThumbnailWorkerState.Running));
    }

    [Fact]
    public async Task A_held_file_storage_period_names_the_check_never_a_path()
    {
        await Recorder.HoldAsync<FileStorageMaintenanceJobRun>(
            JobType.FileStorageMaintenance, JobHeldReason.StorageCheckFailed, run => run.HeldByCheck = "VolumeLost", default);

        var held = (FileStorageMaintenanceJobRun)Assert.Single(await RunsAsync());
        Assert.Equal("VolumeLost", held.HeldByCheck);
    }

    // ── Manual runs ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_manual_request_is_claimed_once_and_finished_into_its_own_row()
    {
        var requested = await RequestAsync<PhotoThumbnailJobRun>(JobType.PhotoThumbnails, Now);
        await Recorder.ExtendBurstAsync<PhotoThumbnailJobRun>(JobType.PhotoThumbnails, run => run.PhotosRendered++, default);

        Assert.Equal(requested, await Recorder.ClaimManualAsync(JobType.PhotoThumbnails, default));
        Assert.Null(await Recorder.ClaimManualAsync(JobType.PhotoThumbnails, default));
        await Recorder.FinishAsync<PhotoThumbnailJobRun>(JobType.PhotoThumbnails, requested, run => run.PhotosRequeued = 2, null, default);

        var runs = await RunsAsync();
        Assert.Equal(2, runs.Count);
        var manual = (PhotoThumbnailJobRun)runs.Single(r => r.JobRunId == requested);
        Assert.Equal((JobRunStatus.Succeeded, 2, Recorder.WorkerId), (manual.Status, manual.PhotosRequeued, manual.WorkerId));
        Assert.Equal(JobRunStatus.Running, runs.Single(r => r.JobRunId != requested).Status);
    }

    // ── Generation (§3.4, AC 9) ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_write_rotates_the_generation_and_a_stale_writer_loses()
    {
        var id = await RequestAsync<WeatherJobRun>(JobType.Weather, Now);
        await using var late = NewContext();
        var stale = await late.JobRuns.SingleAsync(r => r.JobRunId == id);
        var before = stale.Generation;

        await using (var first = NewContext())
        {
            var row = await first.JobRuns.SingleAsync(r => r.JobRunId == id);
            row.Finish(JobRunStatus.Failed, JobFailureCategory.Abandoned, Now);
            await first.SaveChangesAsync();
            Assert.NotEqual(before, row.Generation);
        }

        stale.Finish(JobRunStatus.Succeeded, null, Now);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => late.SaveChangesAsync());
    }

    /// <summary>AC 9: the sweep wrote first; the worker's late finish is dropped and the row keeps the sweep's state.</summary>
    [Fact]
    public async Task A_late_worker_finish_after_the_sweep_is_dropped()
    {
        var id = await RequestAsync<WeatherJobRun>(JobType.Weather, Now, claimed: true);
        clock.Advance(JobCatalogue.Get(JobType.Weather).StaleAfter + TimeSpan.FromMinutes(1));
        await SweepAsync();

        await Recorder.FinishAsync<WeatherJobRun>(JobType.Weather, id, run => run.PointsAttempted = 9, null, default);

        var run = (WeatherJobRun)Assert.Single(await RunsAsync());
        Assert.Equal((JobRunStatus.Failed, JobFailureCategory.Abandoned, 0), (run.Status, run.FailureCategory, run.PointsAttempted));
    }

    // ── The sweep (§3.4) ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>AC 29: a burst with activity every tick is never abandoned; one silent past its window is.</summary>
    [Fact]
    public async Task A_burst_with_recent_activity_survives_the_sweep_and_a_silent_one_does_not()
    {
        for (var minute = 0; minute < 50; minute++)
        {
            await Recorder.ExtendBurstAsync<PhotoThumbnailJobRun>(JobType.PhotoThumbnails, run => run.PhotosRendered++, default);
            clock.Advance(TimeSpan.FromMinutes(1));
            await SweepAsync();
        }

        Assert.Equal(JobRunStatus.Running, Assert.Single(await RunsAsync()).Status);

        clock.Advance(JobCatalogue.Get(JobType.PhotoThumbnails).StaleAfter);
        await SweepAsync();
        var abandoned = Assert.Single(await RunsAsync());
        Assert.Equal((JobRunStatus.Failed, JobFailureCategory.Abandoned), (abandoned.Status, abandoned.FailureCategory));
        Assert.Equal(abandoned.LastActivityAtUtc, abandoned.FinishedAtUtc);
    }

    /// <summary>AC 6, 31: a held row is closed only once its process stops heartbeating, at its last heartbeat.</summary>
    [Fact]
    public async Task A_held_row_is_kept_while_its_process_heartbeats_and_closed_at_the_last_beat_after()
    {
        await Recorder.HeartbeatAsync(JobType.FileStorageMaintenance, default);
        await Recorder.HoldAsync<FileStorageMaintenanceJobRun>(
            JobType.FileStorageMaintenance, JobHeldReason.StorageCheckFailed, run => run.HeldByCheck = "VolumeLost", default);
        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await Recorder.HeartbeatAsync(JobType.FileStorageMaintenance, default);
            await SweepAsync();
        }

        Assert.Null(Assert.Single(await RunsAsync()).FinishedAtUtc);
        var lastBeat = Now;

        // The container restarts: the old process falls silent.
        clock.Advance(TimeSpan.FromSeconds(JobCatalogue.Get(JobType.FileStorageMaintenance).NotTickingAfterSeconds + 1));
        await SweepAsync();

        var closed = Assert.Single(await RunsAsync());
        Assert.Equal((JobRunStatus.Held, lastBeat), (closed.Status, closed.FinishedAtUtc));
    }

    /// <summary>A held row whose process never heartbeat is closed at its own start (S11).</summary>
    [Fact]
    public async Task A_held_row_without_any_heartbeat_closes_at_its_start()
    {
        await Recorder.HoldAsync<PhotoThumbnailJobRun>(JobType.PhotoThumbnails, JobHeldReason.StorageUnavailable, null, default);
        var started = Now;
        clock.Advance(TimeSpan.FromMinutes(10));
        await SweepAsync();

        Assert.Equal(started, Assert.Single(await RunsAsync()).FinishedAtUtc);
    }

    [Fact]
    public async Task A_manual_request_no_worker_takes_is_abandoned_after_two_minutes()
    {
        await RequestAsync<WeatherJobRun>(JobType.Weather, Now);
        clock.Advance(TimeSpan.FromMinutes(1));
        await SweepAsync();
        Assert.Equal(JobRunStatus.Running, Assert.Single(await RunsAsync()).Status);

        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        await SweepAsync();
        var run = Assert.Single(await RunsAsync());
        Assert.Equal((JobRunStatus.Failed, JobFailureCategory.Abandoned), (run.Status, run.FailureCategory));
        Assert.Equal(run.StartedAtUtc + JobCatalogue.UnclaimedRequestTimeout, run.FinishedAtUtc);
    }

    /// <summary>S4: a file-storage pass is never abandoned while the maintenance lease is live.</summary>
    [Fact]
    public async Task The_sweep_never_abandons_a_file_storage_pass_under_a_live_lease()
    {
        var id = await RequestAsync<FileStorageMaintenanceJobRun>(JobType.FileStorageMaintenance, Now, claimed: true);
        clock.Advance(TimeSpan.FromMinutes(30));
        await using (var context = NewContext())
        {
            context.FileStorageMaintenanceLeases.Add(new FileStorageMaintenanceLease
            {
                Id = FileStorageMaintenanceLease.SingletonId, HolderId = Guid.NewGuid(), LeaseUntilUtc = Now.AddMinutes(4), Version = Guid.NewGuid(),
            });
            await context.SaveChangesAsync();
        }

        await SweepAsync();
        Assert.Equal(JobRunStatus.Running, (await RunsAsync()).Single(r => r.JobRunId == id).Status);

        clock.Advance(TimeSpan.FromMinutes(5));
        await SweepAsync();
        Assert.Equal(JobRunStatus.Failed, (await RunsAsync()).Single(r => r.JobRunId == id).Status);
    }

    [Fact]
    public async Task An_abandoned_exchange_rate_sync_run_keeps_its_error_kind_in_step()
    {
        var id = await RequestAsync<ExchangeRateSyncJobRun>(JobType.ExchangeRateSync, Now, claimed: true);
        clock.Advance(JobCatalogue.Get(JobType.ExchangeRateSync).StaleAfter + TimeSpan.FromSeconds(1));
        await SweepAsync();

        var run = (ExchangeRateSyncJobRun)Assert.Single(await RunsAsync());
        Assert.Equal((ExchangeRateSyncErrorKind.Abandoned, JobFailureCategory.Abandoned), (run.ErrorKind!.Value, run.FailureCategory!.Value));
        Assert.Equal(run.StartedAtUtc.AddMinutes(ExchangeRateSyncConstants.RunTimeoutMinutes), run.FinishedAtUtc);
    }

    [Fact]
    public async Task Old_heartbeats_go_unless_their_process_has_an_open_row()
    {
        await using (var context = NewContext())
        {
            var orphan = Guid.NewGuid();
            var holder = Guid.NewGuid();
            context.JobHeartbeats.AddRange(
                new JobHeartbeat { JobType = JobType.Weather, WorkerId = orphan, LastTickAtUtc = Now.AddDays(-2) },
                new JobHeartbeat { JobType = JobType.PhotoThumbnails, WorkerId = holder, LastTickAtUtc = Now.AddDays(-2) });
            context.JobRuns.Add(new PhotoThumbnailJobRun
            {
                JobRunId = Guid.NewGuid(), Status = JobRunStatus.Running, StartedAtUtc = Now, LastActivityAtUtc = Now,
                ClaimedAtUtc = Now, WorkerId = holder,
            });
            await context.SaveChangesAsync();
        }

        await SweepAsync();

        await using var verify = NewContext();
        Assert.Equal(JobType.PhotoThumbnails, Assert.Single(await verify.JobHeartbeats.ToListAsync()).JobType);
    }

    // ── Retention (§3.6, AC 10) ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Finished_runs_past_the_retention_are_pruned_in_batches_and_open_rows_never_are()
    {
        await using (var context = NewContext())
        {
            context.SystemSettings.Add(new SystemSetting { Key = SystemSettingsKeys.JobRunRetentionDays, Value = "30", UpdatedAt = Now });
            var old = Now.AddDays(-31);
            for (var i = 0; i < JobRunSweeper.PruneBatchSize + 5; i++)
            {
                var run = new WeatherJobRun { JobRunId = Guid.NewGuid(), StartedAtUtc = old.AddSeconds(i), LastActivityAtUtc = old };
                run.Finish(JobRunStatus.Succeeded, null, old.AddSeconds(i + 1));
                context.JobRuns.Add(run);
            }

            context.JobRuns.AddRange(
                new WeatherJobRun { JobRunId = Guid.NewGuid(), Status = JobRunStatus.Running, StartedAtUtc = old, LastActivityAtUtc = old },
                new PhotoThumbnailJobRun
                {
                    JobRunId = Guid.NewGuid(), Status = JobRunStatus.Held, HeldReason = JobHeldReason.DecoderPaused,
                    StartedAtUtc = old, LastActivityAtUtc = old, WorkerId = Guid.NewGuid(),
                });
            var recent = new WeatherJobRun { JobRunId = Guid.NewGuid(), StartedAtUtc = Now.AddDays(-29), LastActivityAtUtc = Now };
            recent.Finish(JobRunStatus.Failed, JobFailureCategory.Internal, Now.AddDays(-29));
            context.JobRuns.Add(recent);
            await context.SaveChangesAsync();
        }

        await using var scope = services.CreateAsyncScope();
        var pruned = await scope.ServiceProvider.GetRequiredService<JobRunSweeper>().PruneAsync(default);

        Assert.Equal(JobRunSweeper.PruneBatchSize + 5, pruned);
        var left = await RunsAsync();
        Assert.Equal(3, left.Count);
        Assert.Contains(left, r => r.Status == JobRunStatus.Running);
        Assert.Contains(left, r => r.Status == JobRunStatus.Held && r.FinishedAtUtc is null);
    }

    /// <summary>
    /// Architect review on PR #418: a batch that fails on a concurrency conflict ends the pass. Re-reading would
    /// find the same rows, so a replica that kept losing would spin; the next hourly pass picks up the rest.
    /// </summary>
    [Fact]
    public async Task A_prune_batch_that_loses_a_concurrency_race_ends_the_pass()
    {
        var name = Guid.NewGuid().ToString();
        var conflicts = new AlwaysConflicts();
        await using (var seed = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase(name).Options))
        {
            var old = Now.AddDays(-SystemSettingsDefaults.JobRunRetentionDays - 1);
            for (var i = 0; i < JobRunSweeper.PruneBatchSize + 5; i++)
            {
                var run = new WeatherJobRun { JobRunId = Guid.NewGuid(), StartedAtUtc = old.AddSeconds(i), LastActivityAtUtc = old };
                run.Finish(JobRunStatus.Succeeded, null, old.AddSeconds(i + 1));
                seed.JobRuns.Add(run);
            }

            await seed.SaveChangesAsync();
        }

        await using var context = new OdysseyContext(
            new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase(name).AddInterceptors(conflicts).Options);
        var sweeper = new JobRunSweeper(context, new MemoryCache(new MemoryCacheOptions()), clock, NullLogger<JobRunSweeper>.Instance);

        var pruned = await sweeper.PruneAsync(default).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, pruned);
        Assert.Equal(1, conflicts.Attempts);
    }

    private sealed class AlwaysConflicts : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public int Attempts { get; private set; }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new DbUpdateConcurrencyException("Another replica pruned these rows.");
        }
    }

    /// <summary>
    /// The retention floor keeps the exchange-rate sync's schedule anchor: the sync reads its next run and its failure
    /// streak from its newest finished rows, so the shortest retention must outlast two of its longest gaps.
    /// </summary>
    [Fact]
    public void The_retention_floor_outlasts_two_of_the_syncs_longest_gaps()
    {
        var longestGapHours = Math.Max(SystemSettingsBounds.ExchangeRateSyncIntervalHoursMax, ExchangeRateSyncConstants.MaxBackoffHours);

        Assert.Equal(14, SystemSettingsBounds.JobRunRetentionDaysMin);
        Assert.True(SystemSettingsBounds.JobRunRetentionDaysMin * 24 >= 2 * longestGapHours);
        Assert.InRange(SystemSettingsDefaults.JobRunRetentionDays, SystemSettingsBounds.JobRunRetentionDaysMin, SystemSettingsBounds.JobRunRetentionDaysMax);
    }

    /// <summary>A18: a degraded read never prunes more — it keeps the larger value.</summary>
    [Theory]
    [InlineData(null, SystemSettingsDefaults.JobRunRetentionDays)]
    [InlineData("3", SystemSettingsBounds.JobRunRetentionDaysMin)]
    [InlineData("9999", SystemSettingsBounds.JobRunRetentionDaysMax)]
    [InlineData("not-a-number", SystemSettingsDefaults.JobRunRetentionDays)]
    public async Task The_retention_resolves_through_the_shared_resolver(string? stored, int expected)
    {
        if (stored is not null)
        {
            await using var context = NewContext();
            context.SystemSettings.Add(new SystemSetting { Key = SystemSettingsKeys.JobRunRetentionDays, Value = stored, UpdatedAt = Now });
            await context.SaveChangesAsync();
        }

        await using var scope = services.CreateAsyncScope();
        Assert.Equal(expected, await scope.ServiceProvider.GetRequiredService<JobRunSweeper>().ResolveRetentionDaysAsync(default));
    }

    [Fact]
    public async Task The_maintenance_job_sweeps_every_run_and_prunes_once_an_hour()
    {
        var job = new JobRunMaintenanceJob(services.GetRequiredService<IServiceScopeFactory>(), clock);
        Assert.Equal(TimeSpan.FromMinutes(5), ((IPeriodicJob)job).Interval);

        await RequestAsync<WeatherJobRun>(JobType.Weather, Now);
        clock.Advance(TimeSpan.FromMinutes(3));
        await job.RunOnceAsync(default);

        Assert.Equal(JobRunStatus.Failed, Assert.Single(await RunsAsync()).Status);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────

    private async Task SweepAsync()
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobRunSweeper>().SweepAsync(null, default);
    }

    private async Task<Guid> RequestAsync<TRun>(JobType jobType, DateTime at, bool claimed = false)
        where TRun : JobRun, new()
    {
        await using var context = NewContext();
        var run = new TRun
        {
            JobRunId = Guid.NewGuid(),
            Trigger = JobRunTrigger.Manual,
            Status = JobRunStatus.Running,
            StartedAtUtc = at,
            LastActivityAtUtc = at,
            ClaimedAtUtc = claimed ? at : null,
            WorkerId = claimed ? Guid.NewGuid() : null,
            RequestedByUserId = "admin",
        };
        Assert.Equal(jobType, run.JobType);
        context.JobRuns.Add(run);
        await context.SaveChangesAsync();
        return run.JobRunId;
    }
}
