using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Imaging;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Dtos.Journal;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.Thumbnails;

/// <summary>
/// The thumbnail job on EF InMemory with a fake renderer (issue #362 test tiers, "Job logic"): the pipeline, the
/// failure codes, the lanes, the watchdog, the crash rule, the breaker, the outage and the heartbeat.
/// </summary>
public class PhotoThumbnailJobTests : PhotoThumbnailTestBase
{
    // ── Pipeline (AC 1, 2, 3, 6) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_run_writes_both_sizes_into_bucket_one_with_the_photos_stem_and_marks_the_job_done()
    {
        var host = await NewThumbnailHostAsync();
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Jpeg(4000, 3000, orientation: 6));
        Assert.Equal(PhotoThumbnailJobState.Pending, (await JobRowAsync(host, photoId)).State);

        await Job(host).RunOnceAsync(default);

        var job = await JobRowAsync(host, photoId);
        Assert.Equal((PhotoThumbnailJobState.Done, 1, PhotoThumbnailRecipe.Version), (job.State, job.Attempts, job.AttemptedRecipeVersion));
        Assert.Null(job.RunId);
        var rows = await ThumbnailsAsync(host, photoId);
        Assert.Equal([ThumbnailSize.Small, ThumbnailSize.Medium], rows.Select(r => r.Size));
        Assert.Equal((150, 200), (rows[0].PixelWidth, rows[0].PixelHeight));
        Assert.Equal((300, 400), (rows[1].PixelWidth, rows[1].PixelHeight));
        Assert.Equal("photo-thumbnails/200/1/IMG_2041.webp", (await StoredAsync(host, rows[0].StoredObjectId)).RelativePath);
        Assert.Equal("photo-thumbnails/400/1/IMG_2041.webp", (await StoredAsync(host, rows[1].StoredObjectId)).RelativePath);
        Assert.True(File.Exists(Path.Combine(PrimaryRoot, "photo-thumbnails", "400", "1", "IMG_2041.webp")));
        Assert.Contains(photoId, await WithContextAsync(host, c => PhotoThumbnailService.ReadyAsync(c, [photoId], default)));
    }

    [Fact]
    public async Task The_renderer_sees_stripped_bytes_and_the_orientation_read_from_the_full_original()
    {
        var host = await NewThumbnailHostAsync();
        // The EXIF sits behind 64 KB of padding: well past any admin metadata prefix, still read (AC 6).
        await AddPhotoAsync(host, ThumbnailFixtures.Jpeg(4000, 3000, orientation: 6, exifPadding: 60 * 1024));

        await Job(host).RunOnceAsync(default);

        var call = Assert.Single(Renderer.Calls);
        Assert.Equal((ThumbnailSourceFormat.Jpeg, 6), (call.Format, call.Orientation));
        Assert.False(ContainsMarker(call.Bytes, 0xE1), "an APP1 segment reached the decoder");
        Assert.False(ImageContainerWalk.Walk(call.Bytes, "image/jpeg").HasMetadata);
    }

    [Fact]
    public async Task An_image_jpg_content_type_is_walked_and_stripped_like_image_jpeg()
    {
        var host = await NewThumbnailHostAsync();
        await AddPhotoAsync(host, ThumbnailFixtures.Jpeg(100, 80, orientation: 1), contentType: "image/jpg");

        await Job(host).RunOnceAsync(default);

        Assert.False(ContainsMarker(Assert.Single(Renderer.Calls).Bytes, 0xE1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task An_orientation_outside_one_to_eight_renders_unrotated(int stored)
    {
        var host = await NewThumbnailHostAsync();
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Jpeg(100, 80, orientation: stored));

        await Job(host).RunOnceAsync(default);

        Assert.Equal(1, Assert.Single(Renderer.Calls).Orientation);
        Assert.Equal(PhotoThumbnailJobState.Done, (await JobRowAsync(host, photoId)).State);
    }

    [Fact]
    public async Task Buckets_fill_by_photos_written_never_reopen_and_a_duplicate_name_takes_a_numbered_name()
    {
        var host = await NewThumbnailHostAsync(bucketCapacity: 3);
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await AddPhotoAsync(host, ThumbnailFixtures.Png(150, 100), "image/png", fileName: "IMG_0001.jpg"));
            await Job(host).RunOnceAsync(default);
        }

        string PathOf(PhotoThumbnail t) => StoredAsync(host, t.StoredObjectId).Result.RelativePath;
        var first = await ThumbnailsAsync(host, ids[0]);
        var second = await ThumbnailsAsync(host, ids[1]);
        var fourth = await ThumbnailsAsync(host, ids[3]);
        Assert.Equal("photo-thumbnails/400/1/IMG_0001.webp", PathOf(first[1]));
        Assert.Equal("photo-thumbnails/400/1/IMG_0001 (2).webp", PathOf(second[1]));
        Assert.Equal("photo-thumbnails/400/2/IMG_0001.webp", PathOf(fourth[1]));

        // A delete never reopens bucket 1: the fifth photo goes to bucket 2.
        await using (var scope = host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<PhotoThumbnailService>().StageDeleteAsync(ids[0], default);
            await scope.ServiceProvider.GetRequiredService<OdysseyContext>().SaveChangesAsync();
        }
        var fifth = await AddPhotoAsync(host, ThumbnailFixtures.Png(150, 100), "image/png", fileName: "IMG_0005.png");
        await Job(host).RunOnceAsync(default);
        Assert.StartsWith("photo-thumbnails/400/2/", PathOf((await ThumbnailsAsync(host, fifth))[1]));
    }

    // ── Failure codes (AC 11) ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("application/pdf", "type")]
    [InlineData("image/png", "too-large")]
    public async Task A_file_outside_the_limits_is_unsupported_without_a_decode(string contentType, string code)
    {
        var host = await NewThumbnailHostAsync();
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Png(20_000, 20_000), contentType);

        await Job(host).RunOnceAsync(default);

        var job = await JobRowAsync(host, photoId);
        Assert.Equal((PhotoThumbnailJobState.Unsupported, code), (job.State, job.FailureCode));
        Assert.Empty(Renderer.Calls);
    }

    [Fact]
    public async Task A_large_progressive_jpeg_is_too_large_before_any_native_call_while_baseline_passes()
    {
        var host = await NewThumbnailHostAsync();
        var progressive = await AddPhotoAsync(host, ThumbnailFixtures.Jpeg(8000, 6000, progressive: true));
        var baseline = await AddPhotoAsync(host, ThumbnailFixtures.Jpeg(8000, 6000));

        await Job(host).RunOnceAsync(default);

        Assert.Equal(PhotoThumbnailFailure.TooLarge, (await JobRowAsync(host, progressive)).FailureCode);
        Assert.Equal(PhotoThumbnailJobState.Done, (await JobRowAsync(host, baseline)).State);
        Assert.Single(Renderer.Calls);
    }

    [Fact]
    public async Task An_undecodable_container_is_terminal_and_never_reaches_the_renderer()
    {
        var host = await NewThumbnailHostAsync();
        var truncated = ThumbnailFixtures.Jpeg(100, 80)[..^6];
        var photoId = await AddPhotoAsync(host, truncated);

        await Job(host).RunOnceAsync(default);

        Assert.Equal(PhotoThumbnailFailure.Decode, (await JobRowAsync(host, photoId)).FailureCode);
        Assert.Empty(Renderer.Calls);
    }

    [Theory]
    [InlineData("format")]
    [InlineData("decode")]
    public async Task A_renderer_failure_code_or_exception_ends_unsupported(string code)
    {
        var host = await NewThumbnailHostAsync();
        Renderer.Behaviour = code == "format"
            ? _ => ThumbnailRenderResult.Failed(PhotoThumbnailFailure.Format)
            : _ => throw new InvalidOperationException("decoder text that must not be logged");
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif", fileName: "anim.gif");

        await Job(host).RunOnceAsync(default);

        Assert.Equal((PhotoThumbnailJobState.Unsupported, code), ((await JobRowAsync(host, photoId)).State, (await JobRowAsync(host, photoId)).FailureCode));
        Assert.DoesNotContain(host.Logs.Entries, e => e.Message.Contains("decoder text", StringComparison.Ordinal));
        Assert.Empty(await ThumbnailsAsync(host, photoId));
    }

    // ── Watchdog, bounded pause, restart limit (AC 11, 14, 30) ─────────────────────────────────────────

    [Fact]
    public async Task A_hung_decode_times_out_pauses_claiming_while_it_lives_then_resumes()
    {
        var host = await NewThumbnailHostAsync();
        var hung = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        Renderer.Hang = new ManualResetEventSlim();
        var run = Job(host).RunOnceAsync(default);
        await UntilAsync(() => Renderer.Entered.IsSet, "the decode to start");
        Clock.Advance(PhotoThumbnailTiming.Watchdog + TimeSpan.FromSeconds(1));
        await run;

        var job = await JobRowAsync(host, hung);
        Assert.Equal((PhotoThumbnailJobState.Unsupported, PhotoThumbnailFailure.Timeout), (job.State, job.FailureCode));
        Assert.Equal(1, await WithContextAsync(host, c => c.PhotoThumbnailIncidents.CountAsync(i => i.Kind == PhotoThumbnailIncidentKind.Timeout)));
        Assert.Equal(ThumbnailWorkerState.Paused, Job(host).State);

        var next = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        await Job(host).RunOnceAsync(default);
        Assert.Equal(PhotoThumbnailJobState.Pending, (await JobRowAsync(host, next)).State);

        Renderer.Hang.Set();
        await Task.Delay(200);
        await Job(host).RunOnceAsync(default);
        Assert.Equal(PhotoThumbnailJobState.Done, (await JobRowAsync(host, next)).State);
        Assert.Equal(ThumbnailWorkerState.Running, Job(host).State);
    }

    [Fact]
    public async Task A_decode_hung_past_the_bounded_pause_restarts_the_worker_once_an_hour()
    {
        var host = await NewThumbnailHostAsync();
        await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        Renderer.Hang = new ManualResetEventSlim();
        var run = Job(host).RunOnceAsync(default);
        await UntilAsync(() => Renderer.Entered.IsSet, "the decode to start");
        Clock.Advance(PhotoThumbnailTiming.Watchdog + TimeSpan.FromSeconds(1));
        await run;

        Clock.Advance(PhotoThumbnailTiming.BoundedPause);
        await Job(host).RunOnceAsync(default);
        Assert.Equal(1, Lifetime.StopCalls);
        Assert.Equal(ThumbnailWorkerState.Paused, await HeartbeatStateAsync(host, "w1"));
        Assert.Equal(1, await WithContextAsync(host, c => c.PhotoThumbnailIncidents.CountAsync(i => i.Kind == PhotoThumbnailIncidentKind.Restart && i.WorkerName == "w1")));
        Clock.Advance(PhotoThumbnailTiming.ExitGrace);
        await UntilAsync(() => Exits.Count == 1, "the exit fallback");
        Assert.Equal(70, Exits[0]);

        // The restarted w1 (same host name) hits a second stuck thread within the hour: it stays paused.
        var restarted = await NewThumbnailHostAsync();
        await AddPhotoAsync(restarted, ThumbnailFixtures.Gif(), "image/gif");
        Renderer.Entered.Reset();
        var again = Job(restarted).RunOnceAsync(default);
        await UntilAsync(() => Renderer.Entered.IsSet, "the second decode to start");
        Clock.Advance(PhotoThumbnailTiming.Watchdog + TimeSpan.FromSeconds(1));
        await again;
        Clock.Advance(PhotoThumbnailTiming.BoundedPause);
        await Job(restarted).RunOnceAsync(default);
        Assert.Equal(1, Lifetime.StopCalls);
        Renderer.Hang.Set();
    }

    [Fact]
    public async Task Another_worker_still_restarts_once_within_that_hour()
    {
        var host = await NewThumbnailHostAsync(workerName: "w2");
        await WithContextAsync(host, async c =>
        {
            c.PhotoThumbnailIncidents.Add(new PhotoThumbnailIncident { Kind = PhotoThumbnailIncidentKind.Restart, OccurredAtUtc = Clock.GetUtcNow().UtcDateTime, WorkerName = "w1" });
            await c.SaveChangesAsync();
        });
        await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        Renderer.Hang = new ManualResetEventSlim();
        var run = Job(host).RunOnceAsync(default);
        await UntilAsync(() => Renderer.Entered.IsSet, "the decode to start");
        Clock.Advance(PhotoThumbnailTiming.Watchdog + TimeSpan.FromSeconds(1));
        await run;
        Clock.Advance(PhotoThumbnailTiming.BoundedPause);

        await Job(host).RunOnceAsync(default);

        Assert.Equal(1, Lifetime.StopCalls);
        Renderer.Hang.Set();
    }

    // ── Crash rule, give-back, breaker (AC 14) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_second_expired_lease_ends_unsupported_crash_with_attempts_counted_at_claim()
    {
        var host = await NewThumbnailHostAsync();
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        var job = Job(host);

        for (var expiry = 1; expiry <= 2; expiry++)
        {
            Assert.NotNull(await job.ClaimAsync(default)); // the process "dies" holding the claim
            Assert.Equal(expiry, (await JobRowAsync(host, photoId)).Attempts);
            Clock.Advance(PhotoThumbnailTiming.Lease + TimeSpan.FromSeconds(1));
            await job.ReclaimExpiredAsync(default);
        }

        var row = await JobRowAsync(host, photoId);
        Assert.Equal((PhotoThumbnailJobState.Unsupported, PhotoThumbnailFailure.Crash, 2), (row.State, row.FailureCode, row.LeaseExpiries));
        Assert.Equal(2, await WithContextAsync(host, c => c.PhotoThumbnailIncidents.CountAsync(i => i.Kind == PhotoThumbnailIncidentKind.Crash)));

        // A recipe bump never re-arms it.
        await WithContextAsync(host, async c =>
        {
            (await c.PhotoThumbnailJobs.SingleAsync()).AttemptedRecipeVersion = 0;
            await c.SaveChangesAsync();
        });
        await job.DiscoverAsync(default);
        Assert.Equal(PhotoThumbnailJobState.Unsupported, (await JobRowAsync(host, photoId)).State);
    }

    [Fact]
    public async Task A_live_worker_whose_run_was_reclaimed_gives_the_expiry_back_once_and_removes_its_incident()
    {
        var host = await NewThumbnailHostAsync();
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        var job = Job(host);
        var claimed = (await job.ClaimAsync(default))!;

        // The worker is alive but cannot reach the database for longer than the lease: another worker reclaims it.
        Clock.Advance(PhotoThumbnailTiming.Lease + TimeSpan.FromSeconds(1));
        var other = (await NewThumbnailHostAsync(workerName: "w2")).Services.GetRequiredService<PhotoThumbnailJob>();
        await other.ReclaimExpiredAsync(default);
        Assert.Equal(1, (await JobRowAsync(host, photoId)).LeaseExpiries);

        // The live worker comes back and finishes its run: it finds the run no longer its own.
        await job.RunJobAsync(claimed, default);
        var row = await JobRowAsync(host, photoId);
        Assert.Equal((0, (Guid?)null, (long?)null, PhotoThumbnailJobState.Pending), (row.LeaseExpiries, row.ReclaimedRunId, row.ReclaimIncidentId, row.State));
        Assert.Equal(0, await WithContextAsync(host, c => c.PhotoThumbnailIncidents.CountAsync()));
        Assert.Empty(await ThumbnailsAsync(host, photoId));
        Assert.All(await WithContextAsync(host, c => c.StoredObjects.Where(o => o.FeatureKey == PhotoThumbnailStorage.FeatureKey).ToListAsync()),
            o => Assert.Equal(StoredObjectState.PendingDelete, o.State));

        // A second give-back attempt changes nothing, and the count never goes below zero.
        await job.RunJobAsync(claimed, default);
        Assert.Equal(0, (await JobRowAsync(host, photoId)).LeaseExpiries);
    }

    [Fact]
    public async Task Three_incidents_within_an_hour_trip_the_breaker_until_an_hour_after_the_latest()
    {
        var host = await NewThumbnailHostAsync();
        await WithContextAsync(host, async c =>
        {
            for (var i = 0; i < 3; i++)
            {
                c.PhotoThumbnailIncidents.Add(new PhotoThumbnailIncident
                {
                    Kind = i % 2 == 0 ? PhotoThumbnailIncidentKind.Crash : PhotoThumbnailIncidentKind.Timeout,
                    OccurredAtUtc = Clock.GetUtcNow().UtcDateTime.AddMinutes(-20 + (i * 10)),
                });
            }

            await c.SaveChangesAsync();
        });
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");

        await Job(host).RunOnceAsync(default);
        Assert.Equal(PhotoThumbnailJobState.Pending, (await JobRowAsync(host, photoId)).State);
        Assert.Equal(ThumbnailWorkerState.Tripped, Job(host).State);

        Clock.Advance(TimeSpan.FromMinutes(59));
        await Job(host).RunOnceAsync(default);
        Assert.Equal(PhotoThumbnailJobState.Pending, (await JobRowAsync(host, photoId)).State);

        Clock.Advance(TimeSpan.FromMinutes(2));
        await Job(host).RunOnceAsync(default);
        Assert.Equal(PhotoThumbnailJobState.Done, (await JobRowAsync(host, photoId)).State);
    }

    // ── Shutdown and deadline (AC 14) ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_graceful_shutdown_mid_job_releases_it_with_attempts_and_expiries_unchanged()
    {
        HangingStore.Entered.Reset();
        var host = await NewThumbnailHostAsync(configure: WithStore<HangingStore>);
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        using var stopping = new CancellationTokenSource();
        var run = Job(host).RunOnceAsync(stopping.Token);
        await UntilAsync(() => HangingStore.Entered.IsSet, "the write to start");

        await stopping.CancelAsync();
        await run;

        var job = await JobRowAsync(host, photoId);
        Assert.Equal((PhotoThumbnailJobState.Pending, 0, 0), (job.State, job.Attempts, job.LeaseExpiries));
    }

    [Fact]
    public async Task A_write_wedged_past_the_deadline_releases_the_job_with_no_attempt_expiry_or_incident()
    {
        HangingStore.Entered.Reset();
        var host = await NewThumbnailHostAsync(configure: WithStore<HangingStore>);
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        var run = Job(host).RunOnceAsync(default);
        await UntilAsync(() => HangingStore.Entered.IsSet, "the write to start");

        // The heartbeat stays fresh through a long write (v7.1 AC 28): the renewal tick refreshes it.
        Clock.Advance(PhotoThumbnailTiming.RenewEvery);
        await UntilAsync(() => HeartbeatAgeAsync(host).Result < TimeSpan.FromSeconds(70), "the heartbeat to refresh");
        Clock.Advance(PhotoThumbnailTiming.JobDeadline);
        await run;

        var job = await JobRowAsync(host, photoId);
        Assert.Equal((PhotoThumbnailJobState.Pending, 0, 0), (job.State, job.Attempts, job.LeaseExpiries));
        Assert.Equal(0, await WithContextAsync(host, c => c.PhotoThumbnailIncidents.CountAsync()));
    }

    // ── Outage (AC 15) ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task With_the_volume_degraded_nothing_is_claimed_and_claiming_resumes_within_a_tick_of_its_return()
    {
        var host = await NewThumbnailHostAsync();
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        var marker = Path.Combine(PrimaryRoot, StorageNames.MarkerFileName);
        var saved = File.ReadAllBytes(marker);
        File.Delete(marker);
        host.Registry.MarkDegraded(host.Registry.Find("primary")!, new IOException("gone"));

        for (var cycle = 0; cycle < 10; cycle++)
        {
            await Job(host).RunOnceAsync(default);
        }

        var job = await JobRowAsync(host, photoId);
        Assert.Equal((PhotoThumbnailJobState.Pending, 0), (job.State, job.Attempts));
        Assert.Equal(ThumbnailWorkerState.StorageUnavailable, Job(host).State);

        File.WriteAllBytes(marker, saved);
        await Job(host).RunOnceAsync(default);
        Assert.Equal(PhotoThumbnailJobState.Done, (await JobRowAsync(host, photoId)).State);
    }

    [Fact]
    public async Task A_write_refused_with_a_503_goes_back_pending_without_counting_an_attempt()
    {
        var host = await NewThumbnailHostAsync(configure: WithStore<UnavailableStore>);
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");

        for (var cycle = 0; cycle < 10; cycle++)
        {
            await Job(host).RunOnceAsync(default);
            Clock.Advance(PhotoThumbnailTiming.OutageBackoff);
        }

        var job = await JobRowAsync(host, photoId);
        Assert.Equal((PhotoThumbnailJobState.Pending, 0), (job.State, job.Attempts));
    }

    // ── Lanes and discovery (AC 10, 16, 17, 31) ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_new_upload_beats_the_backfill_and_every_fourth_claim_is_from_the_backfill()
    {
        var host = await NewThumbnailHostAsync();
        for (var i = 0; i < 10; i++)
        {
            await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif", withJob: false);
        }

        var job = Job(host);
        await job.DiscoverAsync(default);
        var uploads = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            uploads.Add(await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif", uploader: "u1"));
        }

        var claimed = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            claimed.Add((await job.ClaimAsync(default))!.PhotoId);
        }

        Assert.All(claimed.Take(3), id => Assert.Contains(id, uploads));
        Assert.DoesNotContain(claimed[3], uploads);
    }

    [Fact]
    public async Task One_uploaders_burst_delays_another_upload_by_at_most_one_claim()
    {
        var host = await NewThumbnailHostAsync();
        for (var i = 0; i < 30; i++)
        {
            await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif", uploader: "a");
        }

        var b = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif", uploader: "b");
        Assert.Equal(PhotoThumbnailJobPriority.Interactive, (await JobRowAsync(host, b)).Priority);
        var job = Job(host);
        var first = (await job.ClaimAsync(default))!.PhotoId;
        var second = (await job.ClaimAsync(default))!.PhotoId;

        Assert.Contains(b, new[] { first, second });
    }

    [Fact]
    public async Task An_uploaders_twenty_first_due_upload_is_background()
    {
        var host = await NewThumbnailHostAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 21; i++)
        {
            ids.Add(await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif", uploader: "u1"));
        }

        Assert.Equal(PhotoThumbnailJobPriority.Interactive, (await JobRowAsync(host, ids[19])).Priority);
        Assert.Equal(PhotoThumbnailJobPriority.Background, (await JobRowAsync(host, ids[20])).Priority);
    }

    [Fact]
    public async Task Discovery_creates_a_job_for_every_photo_without_one_and_two_sweeps_make_no_duplicates()
    {
        var host = await NewThumbnailHostAsync();
        for (var i = 0; i < 30; i++)
        {
            await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif", withJob: false);
        }

        await Job(host).DiscoverAsync(default);
        var second = await NewThumbnailHostAsync(workerName: "w2");
        await second.Services.GetRequiredService<PhotoThumbnailJob>().DiscoverAsync(default);

        Assert.Equal(30, await WithContextAsync(host, c => c.PhotoThumbnailJobs.CountAsync(j => j.Priority == PhotoThumbnailJobPriority.Background)));
    }

    [Fact]
    public async Task A_recipe_reset_requeues_finished_jobs_but_never_a_crash_or_timeout_and_keeps_old_thumbnails_serving()
    {
        var host = await NewThumbnailHostAsync();
        var done = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        await Job(host).RunOnceAsync(default);
        var crashed = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        var timedOut = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        await WithContextAsync(host, async c =>
        {
            foreach (var job in await c.PhotoThumbnailJobs.ToListAsync())
            {
                job.AttemptedRecipeVersion = 0;
                job.Attempts = 3;
                job.LeaseExpiries = job.PhotoId == crashed ? 2 : 0;
                if (job.PhotoId != done)
                {
                    job.State = PhotoThumbnailJobState.Unsupported;
                    job.FailureCode = job.PhotoId == crashed ? PhotoThumbnailFailure.Crash : PhotoThumbnailFailure.Timeout;
                }
            }

            await c.SaveChangesAsync();
        });

        await Job(host).DiscoverAsync(default);

        var reset = await JobRowAsync(host, done);
        Assert.Equal((PhotoThumbnailJobState.Pending, PhotoThumbnailJobPriority.Background, 0), (reset.State, reset.Priority, reset.Attempts));
        Assert.Equal(PhotoThumbnailJobState.Unsupported, (await JobRowAsync(host, crashed)).State);
        Assert.Equal(PhotoThumbnailJobState.Unsupported, (await JobRowAsync(host, timedOut)).State);
        Assert.Contains(done, await WithContextAsync(host, c => PhotoThumbnailService.ReadyAsync(c, [done], default)));
    }

    // ── Rename during a run (AC 17) ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_rename_during_a_successful_run_supersedes_it_gives_the_attempt_back_and_releases_its_files()
    {
        var host = await NewThumbnailHostAsync();
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        Renderer.Hang = new ManualResetEventSlim();
        var run = Job(host).RunOnceAsync(default);
        await UntilAsync(() => Renderer.Entered.IsSet, "the decode to start");

        var fileId = await WithContextAsync(host, c => c.Photos.Where(p => p.PhotoId == photoId).Select(p => p.FileId).SingleAsync());
        await using (var scope = host.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<PhotoThumbnailService>().StageRenameAsync(fileId, default);
            await scope.ServiceProvider.GetRequiredService<OdysseyContext>().SaveChangesAsync();
        }

        var running = await JobRowAsync(host, photoId);
        Assert.Equal((PhotoThumbnailJobState.Running, true), (running.State, running.RerunRequested));
        Renderer.Hang.Set();
        await run;

        var job = await JobRowAsync(host, photoId);
        Assert.Equal((PhotoThumbnailJobState.Pending, PhotoThumbnailJobPriority.Background, 0, false), (job.State, job.Priority, job.Attempts, job.RerunRequested));
        Assert.Equal(job.LastClaimedAtUtc + PhotoThumbnailTiming.RenameCooldown, job.NextAttemptAtUtc);
        Assert.Empty(await ThumbnailsAsync(host, photoId));
        Assert.All(await WithContextAsync(host, c => c.StoredObjects.Where(o => o.FeatureKey == PhotoThumbnailStorage.FeatureKey).ToListAsync()),
            o => Assert.Equal((StoredObjectState.PendingDelete, true), (o.State, o.DeleteImmediately)));
    }

    // ── Self-test, heartbeat (AC 13, 19, 26, 29) ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_or_throwing_self_test_reports_policy_failed_and_claims_nothing(bool throws)
    {
        var host = await NewThumbnailHostAsync();
        SelfTest.Behaviour = throws ? () => throw new DllNotFoundException("Magick.Native") : () => new ThumbnailSelfTestResult("mvg");
        var photoId = await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");

        await Job(host).RunOnceAsync(default);

        Assert.Equal(ThumbnailWorkerState.PolicyFailed, await HeartbeatStateAsync(host, "w1"));
        Assert.Equal(PhotoThumbnailJobState.Pending, (await JobRowAsync(host, photoId)).State);
        Assert.Empty(Renderer.Calls);
    }

    [Fact]
    public async Task The_reported_state_is_the_worst_fresh_heartbeat_or_not_running_and_stale_rows_of_others_are_removed()
    {
        var host = await NewThumbnailHostAsync();
        await WithContextAsync(host, async c =>
        {
            c.PhotoThumbnailWorkerHeartbeats.Add(new PhotoThumbnailWorkerHeartbeat { WorkerName = "w1", State = ThumbnailWorkerState.Paused, UpdatedAtUtc = Clock.GetUtcNow().UtcDateTime.AddMinutes(-10) });
            c.PhotoThumbnailWorkerHeartbeats.Add(new PhotoThumbnailWorkerHeartbeat { WorkerName = "gone", State = ThumbnailWorkerState.Tripped, UpdatedAtUtc = Clock.GetUtcNow().UtcDateTime.AddMinutes(-4) });
            c.PhotoThumbnailWorkerHeartbeats.Add(new PhotoThumbnailWorkerHeartbeat { WorkerName = "alive", State = ThumbnailWorkerState.Running, UpdatedAtUtc = Clock.GetUtcNow().UtcDateTime.AddMinutes(-1) });
            await c.SaveChangesAsync();
        });
        Assert.Equal(ThumbnailWorkerState.Running, await WorkerStateAsync(host));

        await Job(host).RunOnceAsync(default);

        // w1's own stale Paused row is overwritten; another worker's 4-minute-old row is removed; a fresh one stays.
        var rows = await WithContextAsync(host, c => c.PhotoThumbnailWorkerHeartbeats.AsNoTracking().ToListAsync());
        Assert.Equal(["alive", "w1"], rows.Select(r => r.WorkerName).Order());
        Assert.Equal(ThumbnailWorkerState.Running, rows.Single(r => r.WorkerName == "w1").State);

        Clock.Advance(PhotoThumbnailTiming.HeartbeatFresh + TimeSpan.FromSeconds(1));
        Assert.Equal(ThumbnailWorkerState.NotRunning, await WorkerStateAsync(host));

        // A graceful stop removes the worker's own row.
        Lifetime.Stop();
        Assert.DoesNotContain(await WithContextAsync(host, c => c.PhotoThumbnailWorkerHeartbeats.AsNoTracking().ToListAsync()), r => r.WorkerName == "w1");
    }

    [Fact]
    public async Task Stats_count_jobs_of_non_archived_photos_only()
    {
        var host = await NewThumbnailHostAsync();
        await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");
        await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif", archived: true);
        await AddPhotoAsync(host, ThumbnailFixtures.Png(20_000, 20_000), "image/png");
        await Job(host).RunOnceAsync(default);
        await AddPhotoAsync(host, ThumbnailFixtures.Gif(), "image/gif");

        var stats = await WithContextAsync(host, c => new PhotoThumbnailService(c, null!, Clock).GetStatsAsync(default));

        Assert.Equal((1, 0, 1, ThumbnailWorkerState.Running), (stats.Pending, stats.Failed, stats.Unsupported, stats.WorkerState));
    }

    private static Task<ThumbnailWorkerState> WorkerStateAsync(FileStorageTestHost host) =>
        WithContextAsync(host, c => new PhotoThumbnailService(c, null!, host.Clock).GetWorkerStateAsync(default));

    private static Task<ThumbnailWorkerState> HeartbeatStateAsync(FileStorageTestHost host, string worker) =>
        WithContextAsync(host, c => c.PhotoThumbnailWorkerHeartbeats.Where(h => h.WorkerName == worker).Select(h => h.State).SingleAsync());

    private Task<TimeSpan> HeartbeatAgeAsync(FileStorageTestHost host) =>
        WithContextAsync(host, async c => Clock.GetUtcNow().UtcDateTime - await c.PhotoThumbnailWorkerHeartbeats.Where(h => h.WorkerName == "w1").Select(h => h.UpdatedAtUtc).SingleAsync());

    private static void WithStore<TStore>(IServiceCollection services) where TStore : class, IObjectStore
    {
        services.AddScoped<ObjectStoreFactory>();
        services.Replace(ServiceDescriptor.Scoped<IObjectStore, TStore>());
    }

    private static bool ContainsMarker(byte[] bytes, byte marker)
    {
        for (var i = 0; i + 1 < bytes.Length; i++)
        {
            if (bytes[i] == 0xFF && bytes[i + 1] == marker)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The real store, with writes that never return until cancelled — or never, for a non-cooperative fake.</summary>
    private sealed class HangingStore(ObjectStoreFactory factory) : IObjectStore
    {
        public static ManualResetEventSlim Entered { get; } = new();

        public async Task<StoredObject> PutAsync(string featureKey, object? layoutArgs, string? originalFileName, string? suffix,
            string contentType, Stream content, long maxBytes, CancellationToken cancellationToken)
        {
            Entered.Set();
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
            throw new InvalidOperationException("unreachable");
        }

        public Task<StoredObjectContent> OpenReadAsync(string featureKey, Guid storedObjectId, CancellationToken cancellationToken) =>
            factory.Store.OpenReadAsync(featureKey, storedObjectId, cancellationToken);

        public void StageRelease(StoredObject storedObject, bool immediate = false) => factory.Store.StageRelease(storedObject, immediate);

        public Task StageReleaseAsync(Guid storedObjectId, bool immediate, CancellationToken cancellationToken) =>
            factory.Store.StageReleaseAsync(storedObjectId, immediate, cancellationToken);
    }

    private sealed class UnavailableStore(ObjectStoreFactory factory) : IObjectStore
    {
        public Task<StoredObject> PutAsync(string featureKey, object? layoutArgs, string? originalFileName, string? suffix,
            string contentType, Stream content, long maxBytes, CancellationToken cancellationToken) =>
            throw new FileStorageUnavailableException("File volume 'primary' is temporarily unavailable.");

        public Task<StoredObjectContent> OpenReadAsync(string featureKey, Guid storedObjectId, CancellationToken cancellationToken) =>
            factory.Store.OpenReadAsync(featureKey, storedObjectId, cancellationToken);

        public void StageRelease(StoredObject storedObject, bool immediate = false) => factory.Store.StageRelease(storedObject, immediate);

        public Task StageReleaseAsync(Guid storedObjectId, bool immediate, CancellationToken cancellationToken) =>
            factory.Store.StageReleaseAsync(storedObjectId, immediate, cancellationToken);
    }

    /// <summary>The real <see cref="ObjectStore"/> for the request scope, built from that scope's services.</summary>
    private sealed class ObjectStoreFactory(IServiceProvider services)
    {
        public IObjectStore Store { get; } = ActivatorUtilities.CreateInstance<ObjectStore>(services);
    }
}
