using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal.Originals;
using Odyssey.Dtos.Application;
using Odyssey.Testing.FileStorage;
using Xunit;

namespace Odyssey.Core.Tests.Thumbnails;

/// <summary>
/// Issue #428 §3.4: the worker job that moves a legacy photo's original out of <c>FileBlob</c> onto the photo store. It
/// verifies before it changes the database, keeps a file something else still uses, parks a photo it cannot move for a
/// deterministic reason and never retries it, and keeps a name the user gave the photo while it was legacy.
/// </summary>
public sealed class PhotoOriginalBackfillJobTests : PhotoThumbnailTestBase
{
    private static PhotoOriginalBackfillJob Backfill(FileStorageTestHost host) =>
        ActivatorUtilities.CreateInstance<PhotoOriginalBackfillJob>(host.Services);

    private static async Task<(Guid PhotoId, Guid FileId)> AddLegacyAsync(
        FileStorageTestHost host, byte[] bytes, string fileName = "Fjord.jpg", DateTime? createdAt = null)
    {
        await using var scope = host.Scope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var (photo, file) = PhotoTestData.StageLegacyPhoto(
            context, bytes, fileName, createdAt: createdAt ?? new DateTime(2019, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        await context.SaveChangesAsync();
        return (photo.PhotoId, file.Id);
    }

    private static Task SetFileAsync(FileStorageTestHost host, Guid fileId, Action<FileMetadata> change) =>
        WithContextAsync(host, async c =>
        {
            change(await c.FileMetadata.SingleAsync(f => f.Id == fileId));
            await c.SaveChangesAsync();
        });

    private static Task<Photo> PhotoAsync(FileStorageTestHost host, Guid photoId) =>
        WithContextAsync(host, c => c.Photos.AsNoTracking().SingleAsync(p => p.PhotoId == photoId));

    private static Task<PhotoOriginalBackfillLease> LeaseAsync(FileStorageTestHost host) =>
        WithContextAsync(host, c => c.PhotoOriginalBackfillLeases.AsNoTracking().SingleAsync());

    private static async Task<string> ParkedCodeAsync(FileStorageTestHost host, Guid photoId) =>
        (await WithContextAsync(host, c => c.PhotoOriginalBackfillFailures.AsNoTracking().SingleAsync(f => f.PhotoId == photoId))).Code;

    private static Task<bool> OriginalsWrittenAsync(FileStorageTestHost host) =>
        WithContextAsync(host, c => c.StoredObjects.AnyAsync(o => o.FeatureKey == PhotoOriginalStorage.FeatureKey));

    /// <summary>The real object store, with a fault to inject on write or on the read-back.</summary>
    private sealed class FaultyStore
    {
        public Func<Exception?>? PutFailure { get; set; }

        public string? ReadBackHash { get; set; }

        public void Install(IServiceCollection services)
        {
            services.AddScoped<ObjectStore>();
            services.Replace(ServiceDescriptor.Scoped<IObjectStore>(sp => new Wrapper(sp.GetRequiredService<ObjectStore>(), this)));
        }

        private sealed class Wrapper(IObjectStore inner, FaultyStore faults) : IObjectStore
        {
            public Task<StoredObject> PutAsync(
                string featureKey, object? layoutArgs, string? originalFileName, string? suffix, string contentType,
                Stream content, long maxBytes, CancellationToken cancellationToken) =>
                faults.PutFailure?.Invoke() is { } failure
                    ? Task.FromException<StoredObject>(failure)
                    : inner.PutAsync(featureKey, layoutArgs, originalFileName, suffix, contentType, content, maxBytes, cancellationToken);

            public Task<StoredObjectContent> OpenReadAsync(string featureKey, Guid storedObjectId, CancellationToken cancellationToken) =>
                inner.OpenReadAsync(featureKey, storedObjectId, cancellationToken);

            public void StageRelease(StoredObject storedObject, bool immediate = false) => inner.StageRelease(storedObject, immediate);

            public Task StageReleaseAsync(Guid storedObjectId, bool immediate, CancellationToken cancellationToken) =>
                inner.StageReleaseAsync(storedObjectId, immediate, cancellationToken);

            public Task<string> ReadBackHashAsync(StoredObject storedObject, CancellationToken cancellationToken) =>
                faults.ReadBackHash is { } hash ? Task.FromResult(hash) : inner.ReadBackHashAsync(storedObject, cancellationToken);
        }
    }

    [Fact]
    public async Task An_unshared_legacy_photo_moves_and_its_file_is_removed()
    {
        var host = await NewThumbnailHostAsync();
        var bytes = PhotoTestData.Jpeg(1);
        var (photoId, fileId) = await AddLegacyAsync(host, bytes);

        await Backfill(host).RunOnceAsync(default);

        var photo = await WithContextAsync(host, c => c.Photos.AsNoTracking().SingleAsync(p => p.PhotoId == photoId));
        Assert.Null(photo.FileId);
        Assert.Equal(fileId, photo.LegacyFileId);
        Assert.Equal(("Fjord.jpg", "image/jpeg", bytes.LongLength, PhotoTestData.Sha256(bytes)),
            (photo.FileName, photo.ContentType, photo.SizeBytes, photo.Sha256Hash));
        var stored = await StoredAsync(host, photo.StoredObjectId!.Value);
        Assert.Equal((StoredObjectState.Committed, PhotoOriginalStorage.FeatureKey), (stored.State, stored.FeatureKey));
        // Upload year + bucket (§3.2): a photo created in 2019 lands under 2019, whatever year the move runs in.
        Assert.StartsWith("photo-originals/2019/", stored.RelativePath, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(Root("primary"), stored.RelativePath)));
        Assert.False(await WithContextAsync(host, c => c.FileMetadata.AnyAsync(f => f.Id == fileId)));
        Assert.False(await WithContextAsync(host, c => c.FileBlob.AnyAsync()));
    }

    [Fact]
    public async Task A_file_attached_elsewhere_is_copied_and_kept()
    {
        var host = await NewThumbnailHostAsync();
        var (photoId, fileId) = await AddLegacyAsync(host, PhotoTestData.Jpeg(2));
        await WithContextAsync(host, async c =>
        {
            c.JournalEntryAttachments.Add(new JournalEntryAttachment { JournalEntryId = Guid.NewGuid(), FileId = fileId, CreatedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
        });

        await Backfill(host).RunOnceAsync(default);

        var photo = await WithContextAsync(host, c => c.Photos.AsNoTracking().SingleAsync(p => p.PhotoId == photoId));
        Assert.NotNull(photo.StoredObjectId);
        Assert.Null(photo.FileId);
        Assert.True(await WithContextAsync(host, c => c.FileMetadata.AnyAsync(f => f.Id == fileId)));
        var lease = await WithContextAsync(host, c => c.PhotoOriginalBackfillLeases.AsNoTracking().SingleAsync());
        Assert.Equal(1, lease.CopiedSharedCount);
    }

    [Fact]
    public async Task A_rename_made_while_legacy_keeps_its_name()
    {
        var host = await NewThumbnailHostAsync();
        var (photoId, _) = await AddLegacyAsync(host, PhotoTestData.Jpeg(3), "IMG_0001.jpg");
        await WithContextAsync(host, async c =>
        {
            (await c.Photos.SingleAsync(p => p.PhotoId == photoId)).FileName = "Summit.jpg";
            await c.SaveChangesAsync();
        });

        await Backfill(host).RunOnceAsync(default);

        var photo = await WithContextAsync(host, c => c.Photos.AsNoTracking().SingleAsync(p => p.PhotoId == photoId));
        Assert.NotNull(photo.StoredObjectId);
        Assert.Equal("Summit.jpg", photo.FileName);
    }

    [Fact]
    public async Task A_hash_mismatch_parks_the_photo_writes_nothing_and_is_never_retried()
    {
        var host = await NewThumbnailHostAsync();
        var (photoId, fileId) = await AddLegacyAsync(host, PhotoTestData.Jpeg(4));
        await WithContextAsync(host, async c =>
        {
            (await c.FileMetadata.SingleAsync(f => f.Id == fileId)).Sha256Hash = new string('0', 64);
            await c.SaveChangesAsync();
        });

        await Backfill(host).RunOnceAsync(default);

        var failure = await WithContextAsync(host, c => c.PhotoOriginalBackfillFailures.AsNoTracking().SingleAsync());
        Assert.Equal((photoId, PhotoOriginalBackfillFailureCodes.Hash), (failure.PhotoId, failure.Code));
        var photo = await WithContextAsync(host, c => c.Photos.AsNoTracking().SingleAsync(p => p.PhotoId == photoId));
        Assert.Equal((fileId, (Guid?)null), (photo.FileId!.Value, photo.StoredObjectId));
        Assert.False(await WithContextAsync(host, c => c.StoredObjects.AnyAsync(o => o.FeatureKey == PhotoOriginalStorage.FeatureKey)));
        Assert.Empty(await Backfill(host).NextCandidatesAsync(default));
    }

    [Fact]
    public async Task A_type_outside_the_photo_allow_list_parks_as_unreadable()
    {
        var host = await NewThumbnailHostAsync();
        var (photoId, fileId) = await AddLegacyAsync(host, PhotoTestData.Jpeg(5));
        await WithContextAsync(host, async c =>
        {
            (await c.FileMetadata.SingleAsync(f => f.Id == fileId)).ContentType = "application/pdf";
            await c.SaveChangesAsync();
        });

        await Backfill(host).RunOnceAsync(default);

        Assert.Equal(PhotoOriginalBackfillFailureCodes.Read,
            (await WithContextAsync(host, c => c.PhotoOriginalBackfillFailures.AsNoTracking().SingleAsync(f => f.PhotoId == photoId))).Code);
    }

    [Fact]
    public async Task A_second_holder_cannot_claim_a_live_lease()
    {
        var host = await NewThumbnailHostAsync();
        var job = Backfill(host);

        Assert.True(await job.TryClaimAsync(Guid.NewGuid(), default));
        Assert.False(await job.TryClaimAsync(Guid.NewGuid(), default));

        Clock.Advance(PhotoOriginalBackfillJob.LeaseDuration + TimeSpan.FromSeconds(1));
        Assert.True(await job.TryClaimAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task A_file_larger_than_the_deploy_ceiling_parks_as_too_large()
    {
        var host = await NewThumbnailHostAsync();
        var (photoId, fileId) = await AddLegacyAsync(host, PhotoTestData.Jpeg(10));
        var ceiling = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileStorageOptions>>().Value.MaxFileSizeBytes;
        await SetFileAsync(host, fileId, f => f.SizeBytes = ceiling + 1);

        await Backfill(host).RunOnceAsync(default);

        Assert.Equal(PhotoOriginalBackfillFailureCodes.TooLarge, await ParkedCodeAsync(host, photoId));
        Assert.False(await OriginalsWrittenAsync(host));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_bytes_or_a_length_that_disagrees_parks_as_unreadable(bool blobMissing)
    {
        var host = await NewThumbnailHostAsync();
        var bytes = PhotoTestData.Jpeg(11);
        var (photoId, fileId) = await AddLegacyAsync(host, bytes);
        if (blobMissing)
        {
            await WithContextAsync(host, async c =>
            {
                c.FileBlob.Remove(await c.FileBlob.SingleAsync());
                await c.SaveChangesAsync();
            });
        }
        else
        {
            await SetFileAsync(host, fileId, f => f.SizeBytes = bytes.LongLength + 1);
        }

        await Backfill(host).RunOnceAsync(default);

        Assert.Equal(PhotoOriginalBackfillFailureCodes.Read, await ParkedCodeAsync(host, photoId));
        Assert.False(await OriginalsWrittenAsync(host));
    }

    [Fact]
    public async Task A_read_back_that_disagrees_with_the_source_parks_as_hash_and_keeps_the_database_file()
    {
        var store = new FaultyStore { ReadBackHash = new string('f', 64) };
        var host = await NewThumbnailHostAsync(configure: store.Install);
        var (photoId, fileId) = await AddLegacyAsync(host, PhotoTestData.Jpeg(12));

        await Backfill(host).RunOnceAsync(default);

        Assert.Equal(PhotoOriginalBackfillFailureCodes.Hash, await ParkedCodeAsync(host, photoId));
        var photo = await PhotoAsync(host, photoId);
        Assert.Equal((fileId, (Guid?)null), (photo.FileId!.Value, photo.StoredObjectId));
        Assert.True(await WithContextAsync(host, c => c.FileMetadata.AnyAsync(f => f.Id == fileId)));
    }

    [Fact]
    public async Task A_storage_outage_parks_nothing_and_backs_off_until_the_delay_has_passed()
    {
        var store = new FaultyStore { PutFailure = () => new IOException("disk gone") };
        var host = await NewThumbnailHostAsync(configure: store.Install);
        var (photoId, _) = await AddLegacyAsync(host, PhotoTestData.Jpeg(13));

        await Backfill(host).RunOnceAsync(default);

        Assert.False(await WithContextAsync(host, c => c.PhotoOriginalBackfillFailures.AnyAsync()));
        var lease = await LeaseAsync(host);
        Assert.Equal(1, lease.ConsecutiveEnvironmentalFailures);
        Assert.Equal(Clock.GetUtcNow().UtcDateTime + PhotoOriginalBackfillJob.Backoff(1), lease.NextAttemptAtUtc);
        Assert.Null(lease.HolderId);

        // The outage clears, but the job keeps to its back-off …
        store.PutFailure = null;
        await Backfill(host).RunOnceAsync(default);
        Assert.Null((await PhotoAsync(host, photoId)).StoredObjectId);

        // … and moves the photo once it has passed, resetting the failure count.
        Clock.Advance(PhotoOriginalBackfillJob.Backoff(1));
        await Backfill(host).RunOnceAsync(default);
        Assert.NotNull((await PhotoAsync(host, photoId)).StoredObjectId);
        lease = await LeaseAsync(host);
        Assert.Equal((0, (DateTime?)null), (lease.ConsecutiveEnvironmentalFailures, lease.NextAttemptAtUtc));
    }

    /// <summary>PR #441 review: an unexpected failure parks that one photo, and the next one still moves in the same run.</summary>
    [Fact]
    public async Task An_unexpected_failure_parks_that_photo_as_error_and_the_run_moves_on()
    {
        var store = new FaultyStore();
        var host = await NewThumbnailHostAsync(configure: store.Install);
        var (older, _) = await AddLegacyAsync(host, PhotoTestData.Jpeg(14), createdAt: new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var (younger, _) = await AddLegacyAsync(host, PhotoTestData.Jpeg(15), createdAt: new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var calls = 0;
        store.PutFailure = () => calls++ == 0 ? new InvalidOperationException("store contract") : null;

        await Backfill(host).RunOnceAsync(default);

        Assert.Equal(PhotoOriginalBackfillFailureCodes.Error, await ParkedCodeAsync(host, older));
        Assert.NotNull((await PhotoAsync(host, younger)).StoredObjectId);
        Assert.Null((await LeaseAsync(host)).NextAttemptAtUtc);
    }

    [Fact]
    public async Task Below_the_reserve_the_job_does_nothing_and_takes_no_lease()
    {
        var host = await NewThumbnailHostAsync();
        var (photoId, _) = await AddLegacyAsync(host, PhotoTestData.Jpeg(16));
        host.Space.Override = (1024L * 1024 * 1024, 1024L * 1024 * 1024 * 1024);

        await Backfill(host).RunOnceAsync(default);

        Assert.Null((await PhotoAsync(host, photoId)).StoredObjectId);
        Assert.Null((await WithContextAsync(host, c => c.PhotoOriginalBackfillLeases.AsNoTracking().FirstOrDefaultAsync()))?.LastRunAtUtc);
    }

    /// <summary>PR #441 review: with nothing left to move, a run reads once and writes nothing — no lease, no probe.</summary>
    [Fact]
    public async Task With_no_legacy_photo_left_a_run_writes_nothing()
    {
        var host = await NewThumbnailHostAsync();
        var (photoId, _) = await AddLegacyAsync(host, PhotoTestData.Jpeg(17));
        await WithContextAsync(host, async c =>
        {
            c.PhotoOriginalBackfillFailures.Add(new PhotoOriginalBackfillFailure
            {
                PhotoId = photoId, Code = PhotoOriginalBackfillFailureCodes.Hash, RecordedAtUtc = DateTime.UtcNow,
            });
            await c.SaveChangesAsync();
        });

        var before = await WithContextAsync(host, c => c.PhotoOriginalBackfillLeases.AsNoTracking().FirstOrDefaultAsync());

        Assert.False(await Backfill(host).AnyWorkAsync(default));
        await Backfill(host).RunOnceAsync(default);

        // The lease row (seeded, or absent where nothing seeded it) is exactly as it was: never claimed, never released.
        var after = await WithContextAsync(host, c => c.PhotoOriginalBackfillLeases.AsNoTracking().FirstOrDefaultAsync());
        Assert.Equal(before?.Version, after?.Version);
        Assert.Null(after?.LastRunAtUtc);
    }

    [Fact]
    public async Task Candidates_are_oldest_first_in_batches_and_skip_a_photo_whose_thumbnails_are_rendering()
    {
        var host = await NewThumbnailHostAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < PhotoOriginalBackfillJob.BatchSize + 2; i++)
        {
            ids.Add((await AddLegacyAsync(host, PhotoTestData.Jpeg(100 + i), createdAt: new DateTime(2010 + i, 1, 1, 0, 0, 0, DateTimeKind.Utc))).PhotoId);
        }

        await WithContextAsync(host, async c =>
        {
            c.PhotoThumbnailJobs.Add(new PhotoThumbnailJobEntry
            {
                PhotoId = ids[0], State = PhotoThumbnailJobState.Running, NextAttemptAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            });
            await c.SaveChangesAsync();
        });

        var candidates = await Backfill(host).NextCandidatesAsync(default);

        Assert.Equal(ids.Skip(1).Take(PhotoOriginalBackfillJob.BatchSize), candidates);
    }

    [Fact]
    public async Task A_second_run_after_a_move_changes_nothing_and_writes_no_second_object()
    {
        var host = await NewThumbnailHostAsync();
        var (photoId, _) = await AddLegacyAsync(host, PhotoTestData.Jpeg(18));

        await Backfill(host).RunOnceAsync(default);
        var first = (await PhotoAsync(host, photoId)).StoredObjectId;
        Assert.NotNull((await LeaseAsync(host)).LastRunAtUtc);
        await Backfill(host).RunOnceAsync(default);

        Assert.Equal(first, (await PhotoAsync(host, photoId)).StoredObjectId);
        Assert.Equal(1, await WithContextAsync(host, c => c.StoredObjects.CountAsync(o => o.FeatureKey == PhotoOriginalStorage.FeatureKey)));
    }

    // ── The run log (issue #409; the Background jobs page) ───────────────────────────────────────

    private static Task<List<PhotoOriginalBackfillJobRun>> RunsAsync(FileStorageTestHost host) =>
        WithContextAsync(host, c => c.Set<PhotoOriginalBackfillJobRun>().AsNoTracking().OrderBy(r => r.StartedAtUtc).ToListAsync());

    [Fact]
    public async Task A_tick_that_moves_opens_a_burst_and_the_next_idle_tick_finishes_it_succeeded()
    {
        var host = await NewThumbnailHostAsync();
        await AddLegacyAsync(host, PhotoTestData.Jpeg(40));
        var (_, sharedFile) = await AddLegacyAsync(host, PhotoTestData.Jpeg(41), createdAt: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await WithContextAsync(host, async c =>
        {
            c.JournalEntryAttachments.Add(new JournalEntryAttachment { JournalEntryId = Guid.NewGuid(), FileId = sharedFile, CreatedAt = DateTime.UtcNow });
            await c.SaveChangesAsync();
        });

        await Backfill(host).RunOnceAsync(default);
        var open = Assert.Single(await RunsAsync(host));
        Assert.Equal((JobRunStatus.Running, JobRunTrigger.Scheduled, 1, 1), (open.Status, open.Trigger, open.PhotosMoved, open.PhotosCopiedShared));

        await Backfill(host).RunOnceAsync(default);

        var run = Assert.Single(await RunsAsync(host));
        Assert.Equal((JobRunStatus.Succeeded, 2, 0), (run.Status, run.ItemsProcessed, run.ItemsFailed));
        Assert.True(await WithContextAsync(host, c => c.JobHeartbeats.AnyAsync(h => h.JobType == JobType.PhotoOriginalBackfill)));
    }

    [Fact]
    public async Task A_burst_that_only_parked_is_failed_as_unsupported_input()
    {
        var host = await NewThumbnailHostAsync();
        var (_, fileId) = await AddLegacyAsync(host, PhotoTestData.Jpeg(42));
        await SetFileAsync(host, fileId, f => f.ContentType = "application/pdf");

        await Backfill(host).RunOnceAsync(default);
        await Backfill(host).RunOnceAsync(default);

        var run = Assert.Single(await RunsAsync(host));
        Assert.Equal((JobRunStatus.Failed, JobFailureCategory.UnsupportedInput, 1), (run.Status, run.FailureCategory, run.PhotosParked));
    }

    [Fact]
    public async Task With_work_left_and_storage_below_the_reserve_the_process_is_held_for_storage()
    {
        var host = await NewThumbnailHostAsync();
        await AddLegacyAsync(host, PhotoTestData.Jpeg(43));
        host.Space.Override = (1024L * 1024 * 1024, 1024L * 1024 * 1024 * 1024);

        await Backfill(host).RunOnceAsync(default);

        var held = Assert.Single(await RunsAsync(host));
        Assert.Equal((JobRunStatus.Held, JobHeldReason.StorageUnavailable), (held.Status, held.HeldReason));
    }

    [Fact]
    public async Task A_back_off_holds_the_process_and_a_manual_run_ends_it_and_records_the_move()
    {
        var store = new FaultyStore { PutFailure = () => new IOException("disk gone") };
        var host = await NewThumbnailHostAsync(configure: store.Install);
        var (photoId, _) = await AddLegacyAsync(host, PhotoTestData.Jpeg(44));

        await Backfill(host).RunOnceAsync(default);  // interrupted: the burst records it, the lease backs off
        store.PutFailure = null;
        await Backfill(host).RunOnceAsync(default);  // backing off: the burst closes, a held period opens

        var runs = await RunsAsync(host);
        Assert.Equal((JobRunStatus.Failed, JobFailureCategory.Storage, 1), (runs[0].Status, runs[0].FailureCategory, runs[0].Interruptions));
        Assert.Equal((JobRunStatus.Held, JobHeldReason.BackingOff), (runs[1].Status, runs[1].HeldReason));

        var manualId = Guid.NewGuid();
        await WithContextAsync(host, async c =>
        {
            var now = Clock.GetUtcNow().UtcDateTime;
            c.JobRuns.Add(new PhotoOriginalBackfillJobRun
            {
                JobRunId = manualId, Trigger = JobRunTrigger.Manual, Status = JobRunStatus.Running,
                StartedAtUtc = now, LastActivityAtUtc = now, Generation = Guid.NewGuid(),
            });
            await c.SaveChangesAsync();
        });

        await Backfill(host).RunOnceAsync(default);

        Assert.NotNull((await PhotoAsync(host, photoId)).StoredObjectId);
        Assert.Null((await LeaseAsync(host)).NextAttemptAtUtc);
        runs = await RunsAsync(host);
        var manual = runs.Single(r => r.JobRunId == manualId);
        Assert.Equal((JobRunStatus.Succeeded, 1), (manual.Status, manual.PhotosMoved));
        Assert.NotNull(runs.Single(r => r.Status == JobRunStatus.Held).FinishedAtUtc);
    }

    [Fact]
    public async Task With_nothing_left_to_move_no_run_or_held_period_is_recorded()
    {
        var host = await NewThumbnailHostAsync();
        host.Space.Override = (1024L * 1024 * 1024, 1024L * 1024 * 1024 * 1024);

        await Backfill(host).RunOnceAsync(default);

        Assert.Empty(await RunsAsync(host));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    [InlineData(9, 60)]
    public void Environmental_failures_back_off_1_5_15_then_60_minutes(int failures, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), PhotoOriginalBackfillJob.Backoff(failures));

    /// <summary>
    /// §3.4.1: the counter is model-derived, so the columns it queries are every key to <c>FileMetadata</c> except the
    /// photo's own. A known set is pinned too, so a regression in the discovery itself (rather than a new table) fails.
    /// </summary>
    [Fact]
    public void The_reference_counter_queries_every_key_to_file_metadata_but_the_photos_own()
    {
        using var context = new OdysseyContext(new DbContextOptionsBuilder<OdysseyContext>().UseInMemoryDatabase("ref-counter").Options);
        var references = FileReferenceCounter.Discover(context);
        var keys = context.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(FileMetadata))
            .Select(fk => (fk.DeclaringEntityType.ClrType, fk.Properties.Single().Name))
            .ToHashSet();

        Assert.DoesNotContain(references, r => r.EntityType == typeof(Photo) && r.PropertyName == nameof(Photo.FileId));
        Assert.Equal(keys.Count - 1, references.Count);
        Assert.All(references, r => Assert.Contains((r.EntityType, r.PropertyName), keys));
        Assert.Contains(new FileReference(typeof(JournalEntryAttachment), nameof(JournalEntryAttachment.FileId)), references);
        Assert.Contains(new FileReference(typeof(Contact), nameof(Contact.AvatarFileId)), references);
    }

    /// <summary>
    /// §3.5: every production storage feature is <c>Graced</c>, so a release always passes through the one deletion
    /// delay an administrator sets. A new feature declared <c>Immediate</c> fails here until that is a decision.
    /// </summary>
    [Fact]
    public void Every_production_storage_feature_is_graced()
    {
        var features = typeof(PhotoOriginalStorage).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(f => f.FieldType == typeof(StorageFeature))
            .Select(f => (StorageFeature)f.GetValue(null)!)
            .ToList();

        Assert.Equal(
            [PhotoOriginalStorage.FeatureKey, Journal.Thumbnails.PhotoThumbnailStorage.FeatureKey],
            features.Select(f => f.Key).Order(StringComparer.Ordinal));
        Assert.All(features, f => Assert.Equal(StorageReleaseMode.Graced, f.ReleaseMode));
    }
}
