using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Jobs;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Thumbnails;

public sealed partial class PhotoThumbnailJob
{
    // ── One run ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders and stores one claimed job (§3.3 step 4) and writes its outcome (§9). The per-job deadline bounds the
    /// read and the writes, never the decode, which has its own watchdog.
    /// </summary>
    internal async Task RunJobAsync(Claimed claimed, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(PhotoThumbnailTiming.JobDeadline, time);
        using var renewal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewing = RenewLeaseAsync(claimed, renewal.Token);
        try
        {
            await RenderAndStoreAsync(claimed, deadline.Token, renewal, renewing, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseAsync(claimed, PhotoThumbnailTiming.ShutdownReleaseAttempts, "the worker is stopping");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            logger.LogWarning("Thumbnail job for photo {PhotoId} passed its {Deadline} deadline outside the decode; it is released.",
                claimed.PhotoId, PhotoThumbnailTiming.JobDeadline);
            await ReleaseAsync(claimed, maxAttempts: null, "the per-job deadline passed");
        }
        catch (Exception ex) when (IsOutage(ex))
        {
            logger.LogWarning("Thumbnail job for photo {PhotoId} hit an outage ({Failure}); it is retried.", claimed.PhotoId, ex.GetType().Name);
            await OutageAsync(claimed);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Thumbnail job for photo {PhotoId} failed ({Failure}).", claimed.PhotoId, ex.GetType().Name);
            await CountedFailureAsync(claimed);
        }
        finally
        {
            await renewal.CancelAsync();
            await SwallowAsync(renewing);
        }
    }

    private async Task RenderAndStoreAsync(
        Claimed claimed, CancellationToken deadline, CancellationTokenSource renewal, Task renewing, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(deadline, cancellationToken);

        // The original by photo id, with the photo's own file name (issue #428 §3.1, §5.4).
        var originals = scope.ServiceProvider.GetRequiredService<Originals.PhotoOriginalSource>();
        var source = await originals.DescribeAsync(claimed.PhotoId, bounded.Token);
        if (source is not { ContentType: { } contentType, FileName: { } fileName })
        {
            // SourceMissing: the photo or its file has gone, and the job cascades with it.
            return;
        }

        if (PhotoThumbnailLimits.Pin(contentType) is not { } format)
        {
            await UnsupportedAsync(claimed, PhotoThumbnailFailure.Type);
            return;
        }

        var content = await originals.ReadAllAsync(claimed.PhotoId, PhotoThumbnailRecipe.MaxSourceBytes, bounded.Token).WaitAsync(bounded.Token);
        if (content is null)
        {
            return;
        }

        if (content.TooLarge)
        {
            await UnsupportedAsync(claimed, PhotoThumbnailFailure.TooLarge);
            return;
        }

        var orientation = await ReadOrientationAsync(content.Bytes);
        var prepared = PhotoThumbnailPreStrip.Prepare(content.Bytes, format);
        content = null;
        if (prepared.FailureCode is { } preFailure)
        {
            await UnsupportedAsync(claimed, preFailure);
            return;
        }

        var (finished, result) = await RenderUnderWatchdogAsync(new ThumbnailRenderRequest(prepared.Bytes, format, orientation));
        if (!finished)
        {
            await TimedOutAsync(claimed);
            return;
        }

        if (result!.FailureCode is { } renderFailure || result.Medium is null || result.Small is null)
        {
            await UnsupportedAsync(claimed, result.FailureCode ?? PhotoThumbnailFailure.Decode);
            return;
        }

        if (await AllocateBucketAsync(bounded.Token) is not { } bucket)
        {
            await ReleaseAsync(claimed, maxAttempts: null, "the bucket counter stayed contended");
            return;
        }

        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var medium = await PutAsync(store, result.Medium, ThumbnailSize.Medium, bucket, fileName, bounded.Token);
        var small = await PutAsync(store, result.Small, ThumbnailSize.Small, bucket, fileName, bounded.Token);

        // The renewal stops before the final save, so the two never race on the run's own row.
        await renewal.CancelAsync();
        await SwallowAsync(renewing);
        await FinishAsync(context, store, claimed, [(ThumbnailSize.Medium, medium, result.Medium), (ThumbnailSize.Small, small, result.Small)], bounded.Token);
    }

    private static async Task<StoredObject> PutAsync(
        IObjectStore store, ThumbnailRendition rendition, ThumbnailSize size, int bucket, string fileName, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(rendition.WebP, writable: false);
        return await store.PutAsync(
                PhotoThumbnailStorage.FeatureKey, new PhotoThumbnailLayout(size, bucket), fileName, null, "image/webp",
                stream, PhotoThumbnailRecipe.MaxOutputBytes, cancellationToken)
            .WaitAsync(cancellationToken);
    }

    /// <summary>
    /// The final save (§3.3 step 4.6): both rows upserted, the replaced objects released, the job <c>Done</c> — or,
    /// if a rename landed meanwhile, the run superseded; if the run was reclaimed, only the give-back. Either way a
    /// run that does not finish releases its own new objects. Retried, uncapped, on any conflict but a swept object.
    /// </summary>
    private async Task FinishAsync(
        OdysseyContext context, IObjectStore store, Claimed claimed,
        IReadOnlyList<(ThumbnailSize Size, StoredObject Object, ThumbnailRendition Rendition)> outputs,
        CancellationToken cancellationToken)
    {
        var ownObjectIds = outputs.Select(o => o.Object.Id).ToHashSet();
        while (true)
        {
            var now = Now;
            var job = await context.PhotoThumbnailJobs.FirstOrDefaultAsync(j => j.PhotoId == claimed.PhotoId, cancellationToken);
            if (job is null)
            {
                // The photo was deleted mid-run: its job cascaded away. The new objects go with it.
                ReleaseOwn(store, outputs);
            }
            else if (job.State != PhotoThumbnailJobState.Running || job.RunId != claimed.RunId)
            {
                ReleaseOwn(store, outputs);
                await context.SaveChangesAsync(cancellationToken);
                await GiveBackExpiryAsync(claimed, cancellationToken);
                return;
            }
            else if (job.RerunRequested)
            {
                // Superseded by a rename: the attempt is given back and the re-run waits out the cooldown.
                ReleaseOwn(store, outputs);
                job.State = PhotoThumbnailJobState.Pending;
                job.Priority = PhotoThumbnailJobPriority.Background;
                job.NextAttemptAtUtc = PhotoThumbnailService.CooldownFrom(job, now);
                job.Attempts = Math.Max(0, job.Attempts - 1);
                EndRun(job, now);
            }
            else
            {
                var existing = await context.PhotoThumbnails.Where(t => t.PhotoId == claimed.PhotoId).ToListAsync(cancellationToken);
                foreach (var (size, storedObject, rendition) in outputs)
                {
                    var row = existing.FirstOrDefault(t => t.Size == size);
                    if (row is null)
                    {
                        context.PhotoThumbnails.Add(new PhotoThumbnail
                        {
                            PhotoThumbnailId = Guid.NewGuid(),
                            PhotoId = claimed.PhotoId,
                            Size = size,
                            StoredObjectId = storedObject.Id,
                            PixelWidth = rendition.Width,
                            PixelHeight = rendition.Height,
                            RecipeVersion = PhotoThumbnailRecipe.Version,
                            CreatedAtUtc = now,
                        });
                        continue;
                    }

                    // Updated in place: a delete and re-insert of (PhotoId, Size) in one save is not ordered by EF.
                    if (row.StoredObjectId != storedObject.Id
                        && await context.StoredObjects.FirstOrDefaultAsync(o => o.Id == row.StoredObjectId, cancellationToken) is { State: StoredObjectState.Committed } replaced)
                    {
                        store.StageRelease(replaced);
                    }

                    row.StoredObjectId = storedObject.Id;
                    row.PixelWidth = rendition.Width;
                    row.PixelHeight = rendition.Height;
                    row.RecipeVersion = PhotoThumbnailRecipe.Version;
                    row.CreatedAtUtc = now;
                }

                job.State = PhotoThumbnailJobState.Done;
                job.FailureCode = null;
                EndRun(job, now);
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                if (job is { State: PhotoThumbnailJobState.Done })
                {
                    tally.Rendered++;
                }

                return;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                if (ex.Entries.Any(e => e.Entity is StoredObject o && ownObjectIds.Contains(o.Id)))
                {
                    // Our own reservation was swept: the files are gone. An outage for this job, not the image's fault.
                    throw new FileStorageUnavailableException("A thumbnail reservation was swept before it was saved.");
                }

                await ResetForRetryAsync(context, ownObjectIds, cancellationToken);
                await JitterAsync(cancellationToken);
            }
            catch (DbUpdateException) when (job is null)
            {
                // The photo went between the load and the save (an FK failure): SourceMissing.
                return;
            }
        }
    }

    /// <summary>Discards everything staged but our own new objects, so the next attempt decides from the database.</summary>
    private static async Task ResetForRetryAsync(OdysseyContext context, HashSet<Guid> ownObjectIds, CancellationToken cancellationToken)
    {
        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is StoredObject o && ownObjectIds.Contains(o.Id))
            {
                continue;
            }

            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
            }
            else if (entry.State is EntityState.Modified or EntityState.Deleted or EntityState.Unchanged)
            {
                await entry.ReloadAsync(cancellationToken);
            }
        }
    }

    private static void ReleaseOwn(IObjectStore store, IEnumerable<(ThumbnailSize Size, StoredObject Object, ThumbnailRendition Rendition)> outputs)
    {
        foreach (var (_, storedObject, _) in outputs)
        {
            if (storedObject.State == StoredObjectState.Committed)
            {
                store.StageRelease(storedObject);
            }
        }
    }

    /// <summary>
    /// Proof of life (§3.3 step 6): a run that was reclaimed while its worker was alive all along gives the expiry
    /// back, once — the reclaim recorded which run it took over, and a crashed process never comes back to say so.
    /// </summary>
    private async Task GiveBackExpiryAsync(Claimed claimed, CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var job = await context.PhotoThumbnailJobs.FirstOrDefaultAsync(j => j.PhotoId == claimed.PhotoId, cancellationToken);
            if (job is null || job.ReclaimedRunId != claimed.RunId)
            {
                return;
            }

            // The count only: a job the reclaim already ended Unsupported (crash) stays so — the give-back must never
            // re-arm a file that crashes the decoder.
            job.LeaseExpiries = Math.Max(0, job.LeaseExpiries - 1);

            if (job.ReclaimIncidentId is { } incidentId
                && await context.PhotoThumbnailIncidents.FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken) is { } incident)
            {
                context.PhotoThumbnailIncidents.Remove(incident);
            }

            job.ReclaimedRunId = null;
            job.ReclaimIncidentId = null;
            Touch(job, Now);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Thumbnail job for photo {PhotoId} was reclaimed while its worker was alive; the expiry is given back.", claimed.PhotoId);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                await JitterAsync(cancellationToken);
            }
        }
    }

    // ── Run-scoped outcome writes ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies <paramref name="apply"/> while the job is still this run's, reloading and deciding again on a conflict.
    /// Returns false when the run is no longer ours, in which case only the give-back is attempted.
    /// </summary>
    private async Task<bool> WriteRunAsync(Claimed claimed, Action<PhotoThumbnailJobEntry, DateTime> apply, int? maxAttempts = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var job = await context.PhotoThumbnailJobs.FirstOrDefaultAsync(j => j.PhotoId == claimed.PhotoId);
            if (job is null)
            {
                return false;
            }

            if (job.State != PhotoThumbnailJobState.Running || job.RunId != claimed.RunId)
            {
                await GiveBackExpiryAsync(claimed, CancellationToken.None);
                return false;
            }

            var now = Now;
            apply(job, now);
            Touch(job, now);
            try
            {
                await context.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateConcurrencyException) when (maxAttempts is null || attempt < maxAttempts)
            {
                await JitterAsync(CancellationToken.None);
            }
        }
    }

    private async Task<bool> UnsupportedAsync(Claimed claimed, string code)
    {
        logger.LogWarning("Thumbnail job for photo {PhotoId} is unsupported ({Code}).", claimed.PhotoId, code);
        var landed = await WriteRunAsync(claimed, (job, now) =>
        {
            job.State = PhotoThumbnailJobState.Unsupported;
            job.FailureCode = code;
            EndRun(job, now);
        });
        if (landed)
        {
            tally.Failed++;
        }

        return landed;
    }

    /// <summary>A storage or database outage is not the image's fault: the attempt is given back (§3.3 step 5).</summary>
    private Task<bool> OutageAsync(Claimed claimed) => WriteRunAsync(claimed, (job, now) =>
    {
        job.State = PhotoThumbnailJobState.Pending;
        job.Attempts = Math.Max(0, job.Attempts - 1);
        job.NextAttemptAtUtc = now + PhotoThumbnailTiming.OutageBackoff;
        EndRun(job, now);
    });

    private async Task<bool> CountedFailureAsync(Claimed claimed)
    {
        var landed = await CountedFailureWriteAsync(claimed);
        if (landed)
        {
            tally.Failed++;
        }

        return landed;
    }

    private Task<bool> CountedFailureWriteAsync(Claimed claimed) => WriteRunAsync(claimed, (job, now) =>
    {
        if (job.Attempts >= PhotoThumbnailTiming.MaxCountedAttempts)
        {
            job.State = PhotoThumbnailJobState.Failed;
            job.FailureCode = PhotoThumbnailFailure.Exhausted;
        }
        else
        {
            job.State = PhotoThumbnailJobState.Pending;
            job.NextAttemptAtUtc = now + PhotoThumbnailTiming.Backoff(job.Attempts);
        }

        EndRun(job, now);
    });

    /// <summary>A shutdown, a deadline or a contended bucket: back to <c>Pending</c>, the attempt given back, no expiry.</summary>
    private async Task ReleaseAsync(Claimed claimed, int? maxAttempts, string why)
    {
        try
        {
            await WriteRunAsync(claimed, (job, now) =>
            {
                job.State = PhotoThumbnailJobState.Pending;
                job.Attempts = Math.Max(0, job.Attempts - 1);
                job.NextAttemptAtUtc = now;
                EndRun(job, now);
            }, maxAttempts);
        }
        catch (Exception ex) when (maxAttempts is not null)
        {
            logger.LogWarning("Releasing thumbnail job for photo {PhotoId} failed ({Failure}); its lease expires on its own.", claimed.PhotoId, ex.GetType().Name);
            return;
        }

        logger.LogInformation("Thumbnail job for photo {PhotoId} was released because {Reason}.", claimed.PhotoId, why);
    }

    /// <summary>
    /// The watchdog fired (§3.6): the job is <c>Unsupported (timeout)</c> first, with its incident only if the write
    /// landed on this run; then the thread is abandoned and claiming pauses while it lives.
    /// </summary>
    private async Task TimedOutAsync(Claimed claimed)
    {
        var landed = await WriteRunAsync(claimed, (job, now) =>
        {
            job.State = PhotoThumbnailJobState.Unsupported;
            job.FailureCode = PhotoThumbnailFailure.Timeout;
            EndRun(job, now);
        });
        if (landed)
        {
            tally.Hangs++;
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.PhotoThumbnailIncidents.Add(new PhotoThumbnailIncident { Kind = PhotoThumbnailIncidentKind.Timeout, OccurredAtUtc = Now });
            await context.SaveChangesAsync();
        }

        logger.LogCritical("The thumbnail decode for photo {PhotoId} exceeded {Watchdog} and was abandoned (timeout).",
            claimed.PhotoId, PhotoThumbnailTiming.Watchdog);
    }

    private static void EndRun(PhotoThumbnailJobEntry job, DateTime now)
    {
        job.RunId = null;
        job.RerunRequested = false;
        job.LeaseUntilUtc = null;
    }

    // ── Bucket ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes a slot in the current bucket (§3.2.1), in its own save before the files are written, so a crash can only
    /// under-fill a bucket. A conflict reloads and retries up to 3 times (v7.1 §1); null after that.
    /// </summary>
    internal async Task<int?> AllocateBucketAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < PhotoThumbnailTiming.BucketAttempts; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var bucket = await context.PhotoThumbnailBuckets.FirstOrDefaultAsync(b => b.Id == PhotoThumbnailBucket.SingletonId, cancellationToken);
            if (bucket is null)
            {
                // The migration seeds the row; a database created without HasData (EF InMemory) gets it here.
                bucket = new PhotoThumbnailBucket { Id = PhotoThumbnailBucket.SingletonId, CurrentBucket = 1 };
                context.PhotoThumbnailBuckets.Add(bucket);
            }

            if (bucket.PhotosWritten >= options.BucketCapacity)
            {
                bucket.CurrentBucket++;
                bucket.PhotosWritten = 0;
            }

            bucket.PhotosWritten++;
            bucket.UpdatedAtUtc = Now;
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return bucket.CurrentBucket;
            }
            catch (DbUpdateException)
            {
                // A concurrent allocation (or the first insert, raced): reload and try again.
            }
        }

        return null;
    }
}
