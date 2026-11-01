using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Thumbnails;

/// <summary>A thumbnail's stored object, for the endpoint's conditional read: the hash and the state, never the path.</summary>
public sealed record PhotoThumbnailDescriptor(Guid StoredObjectId, string Sha256Hash);

/// <summary>The thumbnail counts and worker state for <c>GET /api/photos/stats</c> (§4.4).</summary>
public sealed record PhotoThumbnailStats(int Pending, int Failed, int Unsupported, ThumbnailWorkerState WorkerState, DateTime? ResumesAtUtc = null);

/// <summary>
/// The breaker rule (§3.6), in one place for the job that obeys it and the stats that report it: 3 or more crashes
/// or hangs within an hour stop claiming until an hour after the latest of them.
/// </summary>
public static class PhotoThumbnailBreaker
{
    /// <summary>When claiming resumes, or null when the breaker is not tripped at <paramref name="now"/>.</summary>
    public static async Task<DateTime?> ResumesAtAsync(OdysseyContext context, DateTime now, CancellationToken cancellationToken)
    {
        var since = now - (2 * PhotoThumbnailTiming.BreakerWindow);
        var recent = await context.PhotoThumbnailIncidents
            .Where(i => i.OccurredAtUtc > since
                && (i.Kind == PhotoThumbnailIncidentKind.Crash || i.Kind == PhotoThumbnailIncidentKind.Timeout))
            .Select(i => i.OccurredAtUtc)
            .ToListAsync(cancellationToken);
        return ResumesAt(recent, now);
    }

    public static DateTime? ResumesAt(IReadOnlyCollection<DateTime> incidents, DateTime now)
    {
        DateTime? resumes = null;
        foreach (var latest in incidents.Where(at => now < at + PhotoThumbnailTiming.BreakerWindow))
        {
            var cluster = incidents.Count(at => at <= latest && at > latest - PhotoThumbnailTiming.BreakerWindow);
            if (cluster >= PhotoThumbnailTiming.BreakerThreshold && (resumes is null || latest + PhotoThumbnailTiming.BreakerWindow > resumes))
            {
                resumes = latest + PhotoThumbnailTiming.BreakerWindow;
            }
        }

        return resumes;
    }
}

/// <summary>
/// The thumbnail side of the photo paths that run in the API (issue #362 §3.3, §3.4, §4.1, §5): enqueue on create,
/// release on delete, the rename rule, serving and the stats. Everything it stages lands in the caller's save.
/// </summary>
public sealed class PhotoThumbnailService(OdysseyContext context, IObjectStore store, TimeProvider time)
{
    /// <summary>
    /// Stages the job for a photo just added to the context (§3.3 step 1): <c>Interactive</c> while its uploader has
    /// fewer than 20 due interactive jobs — checked here, best-effort under concurrency — otherwise <c>Background</c>.
    /// </summary>
    public async Task StageEnqueueAsync(Photo photo, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var uploader = photo.CreatedByUserId;
        var dueInteractive = await context.PhotoThumbnailJobs
            .Where(j => j.State == PhotoThumbnailJobState.Pending && j.Priority == PhotoThumbnailJobPriority.Interactive)
            .Join(context.Photos, j => j.PhotoId, p => p.PhotoId, (j, p) => p.CreatedByUserId)
            .CountAsync(u => u == uploader, cancellationToken);

        context.PhotoThumbnailJobs.Add(new PhotoThumbnailJobEntry
        {
            PhotoId = photo.PhotoId,
            State = PhotoThumbnailJobState.Pending,
            Priority = dueInteractive < PhotoThumbnailTiming.InteractivePerUploader
                ? PhotoThumbnailJobPriority.Interactive
                : PhotoThumbnailJobPriority.Background,
            NextAttemptAtUtc = now,
            UpdatedAtUtc = now,
        });
    }

    /// <summary>
    /// Stages an immediate release of both of a photo's thumbnails, in the delete's own save (§4.1). The rows and the
    /// job are removed too rather than left to the cascade, which the EF InMemory provider does not run.
    /// </summary>
    public async Task StageDeleteAsync(Guid photoId, CancellationToken cancellationToken)
    {
        var thumbnails = await context.PhotoThumbnails.Where(t => t.PhotoId == photoId).ToListAsync(cancellationToken);
        await StageReleaseAsync([.. thumbnails.Select(t => t.StoredObjectId)], cancellationToken);
        context.PhotoThumbnails.RemoveRange(thumbnails);
        context.PhotoThumbnailJobs.RemoveRange(
            await context.PhotoThumbnailJobs.Where(j => j.PhotoId == photoId).ToListAsync(cancellationToken));
    }

    /// <summary>
    /// The rename rule (§3.4), staged on the rename's context: the old-name files are released at once, whatever
    /// happens to the regeneration, and the job is re-queued, flagged or left alone by its state. Idempotent, so the
    /// rename's concurrency retry can run it again.
    /// </summary>
    public async Task StageRenameAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var photoId = await context.Photos
            .Where(p => p.FileId == fileId)
            .Select(p => (Guid?)p.PhotoId)
            .FirstOrDefaultAsync(cancellationToken);
        if (photoId is not { } id)
        {
            return;
        }

        var thumbnails = await context.PhotoThumbnails.Where(t => t.PhotoId == id).ToListAsync(cancellationToken);
        await StageReleaseAsync([.. thumbnails.Select(t => t.StoredObjectId)], cancellationToken);
        context.PhotoThumbnails.RemoveRange(thumbnails);

        var job = await context.PhotoThumbnailJobs.FirstOrDefaultAsync(j => j.PhotoId == id, cancellationToken);
        if (job is null)
        {
            return;
        }

        var now = time.GetUtcNow().UtcDateTime;
        switch (job.State)
        {
            case PhotoThumbnailJobState.Done:
            case PhotoThumbnailJobState.Pending:
                job.State = PhotoThumbnailJobState.Pending;
                job.Priority = PhotoThumbnailJobPriority.Background;
                job.NextAttemptAtUtc = CooldownFrom(job, now);
                break;
            case PhotoThumbnailJobState.Running:
                // Never State, RunId, the lease, Attempts or LeaseExpiries: the run's crash, hang or failure must
                // still be recorded, and its success becomes a re-run.
                job.RerunRequested = true;
                break;
            default:
                // Unsupported or Failed: their outputs carry no name, and re-running a file that crashed or hung the
                // decoder would defeat the crash rule.
                return;
        }

        job.Generation++;
        job.UpdatedAtUtc = now;
    }

    /// <summary>
    /// A rename's re-run waits for <c>max(existing, now, last claim + 10 min)</c>, so a rename never pulls a retry or
    /// a backoff forward, and renaming during each run cannot bypass it (§3.3 step 2).
    /// </summary>
    public static DateTime CooldownFrom(PhotoThumbnailJobEntry job, DateTime now)
    {
        var due = job.NextAttemptAtUtc > now ? job.NextAttemptAtUtc : now;
        if (job.LastClaimedAtUtc is { } claimed && claimed + PhotoThumbnailTiming.RenameCooldown > due)
        {
            due = claimed + PhotoThumbnailTiming.RenameCooldown;
        }

        return due;
    }

    /// <summary>
    /// The thumbnail's object, when it can be served: a committed object of this feature. Unknown photo, not ready and
    /// released alike return null, so the endpoint's 404 never says which (§5).
    /// </summary>
    public Task<PhotoThumbnailDescriptor?> GetDescriptorAsync(Guid photoId, ThumbnailSize size, CancellationToken cancellationToken) =>
        context.PhotoThumbnails
            .Where(t => t.PhotoId == photoId && t.Size == size)
            .Join(context.StoredObjects, t => t.StoredObjectId, o => o.Id, (_, o) => o)
            .Where(o => o.State == StoredObjectState.Committed && o.FeatureKey == PhotoThumbnailStorage.FeatureKey && o.Sha256Hash != null)
            .Select(o => new PhotoThumbnailDescriptor(o.Id, o.Sha256Hash!))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The ids among <paramref name="photoIds"/> with both thumbnails, at any recipe version (§4.4).</summary>
    public Task<IReadOnlySet<Guid>> ReadyAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken) =>
        ReadyAsync(context, photoIds, cancellationToken);

    /// <inheritdoc cref="ReadyAsync(IReadOnlyCollection{Guid}, CancellationToken)"/>
    public static async Task<IReadOnlySet<Guid>> ReadyAsync(
        OdysseyContext context, IReadOnlyCollection<Guid> photoIds, CancellationToken cancellationToken)
    {
        if (photoIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ready = await context.PhotoThumbnails
            .Where(t => photoIds.Contains(t.PhotoId))
            .GroupBy(t => t.PhotoId)
            .Where(g => g.Count() == 2)
            .Select(g => g.Key)
            .ToListAsync(cancellationToken);
        return ready.ToHashSet();
    }

    /// <summary>The counts over non-archived photos, and the worst fresh worker state (§3.6, §4.4).</summary>
    public async Task<PhotoThumbnailStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        var counts = await context.PhotoThumbnailJobs
            .Join(context.Photos.Where(p => p.Archived == null), j => j.PhotoId, p => p.PhotoId, (j, _) => j.State)
            .GroupBy(s => s)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        int Count(params PhotoThumbnailJobState[] states) => counts.Where(c => states.Contains(c.State)).Sum(c => c.Count);

        var state = await GetWorkerStateAsync(cancellationToken);
        return new PhotoThumbnailStats(
            Count(PhotoThumbnailJobState.Pending, PhotoThumbnailJobState.Running),
            Count(PhotoThumbnailJobState.Failed),
            Count(PhotoThumbnailJobState.Unsupported),
            state,
            state == ThumbnailWorkerState.Tripped
                ? await PhotoThumbnailBreaker.ResumesAtAsync(context, time.GetUtcNow().UtcDateTime, cancellationToken)
                : null);
    }

    /// <summary>The most severe state among workers heard from in the last 3 minutes, or <c>NotRunning</c> (§3.6).</summary>
    public async Task<ThumbnailWorkerState> GetWorkerStateAsync(CancellationToken cancellationToken)
    {
        var fresh = time.GetUtcNow().UtcDateTime - PhotoThumbnailTiming.HeartbeatFresh;
        var states = await context.PhotoThumbnailWorkerHeartbeats
            .Where(h => h.UpdatedAtUtc > fresh)
            .Select(h => h.State)
            .ToListAsync(cancellationToken);
        return states.Count == 0 ? ThumbnailWorkerState.NotRunning : states.Max();
    }

    private async Task StageReleaseAsync(IReadOnlyCollection<Guid> objectIds, CancellationToken cancellationToken)
    {
        foreach (var objectId in objectIds)
        {
            // Idempotent: an object already staged (a retried rename) or already released is left as it is.
            var tracked = context.StoredObjects.Local.FirstOrDefault(o => o.Id == objectId)
                ?? await context.StoredObjects.FirstOrDefaultAsync(o => o.Id == objectId, cancellationToken);
            if (tracked is { State: StoredObjectState.Committed })
            {
                store.StageRelease(tracked, immediate: true);
            }
        }
    }
}

/// <summary>The Journal side of a file rename (§3.1 item 5): the photo's thumbnails follow its file's name.</summary>
public sealed class PhotoThumbnailRenameObserver(PhotoThumbnailService thumbnails) : Finance.IFileRenameObserver
{
    public Task OnFileRenamingAsync(Guid fileId, CancellationToken cancellationToken) =>
        thumbnails.StageRenameAsync(fileId, cancellationToken);

    /// <summary>The job, the thumbnail rows and their stored objects: everything <see cref="PhotoThumbnailService.StageRenameAsync"/> stages.</summary>
    public bool Owns(Type entityType) =>
        entityType == typeof(PhotoThumbnailJobEntry) || entityType == typeof(PhotoThumbnail) || entityType == typeof(StoredObject);
}
