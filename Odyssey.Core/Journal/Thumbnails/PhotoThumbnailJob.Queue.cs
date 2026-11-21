using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Finance;
using Odyssey.Core.Jobs;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Journal.Thumbnails;

public sealed partial class PhotoThumbnailJob
{
    // ── Discovery and recipe sweep ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The backfill and the recipe sweep (§3.3 step 7): a job for every photo without one, and a reset for every
    /// finished job attempted under an older recipe — except a file that crashed or hung the decoder.
    /// </summary>
    internal async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var missing = await context.Photos
                .Where(p => !context.PhotoThumbnailJobs.Any(j => j.PhotoId == p.PhotoId))
                .OrderBy(p => p.CreatedAt)
                .Select(p => p.PhotoId)
                .Take(PhotoThumbnailTiming.DiscoveryBatch)
                .ToListAsync(cancellationToken);
            if (missing.Count > 0)
            {
                context.PhotoThumbnailJobs.AddRange(missing.Select(id => NewBackgroundJob(id, now)));
                try
                {
                    await context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    // Another replica won some of them (v7.1 §1): insert one at a time, each conflict a success.
                    await InsertOneByOneAsync(missing, now, cancellationToken);
                }
            }
        }

        await using (var scope = scopes.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var stale = await context.PhotoThumbnailJobs
                .Where(j => (j.State == PhotoThumbnailJobState.Done
                        || j.State == PhotoThumbnailJobState.Failed
                        || (j.State == PhotoThumbnailJobState.Unsupported
                            && j.FailureCode != PhotoThumbnailFailure.Crash
                            && j.FailureCode != PhotoThumbnailFailure.Timeout))
                    && j.AttemptedRecipeVersion < PhotoThumbnailRecipe.Version)
                .Take(PhotoThumbnailTiming.DiscoveryBatch)
                .ToListAsync(cancellationToken);
            foreach (var job in stale)
            {
                job.State = PhotoThumbnailJobState.Pending;
                job.Priority = PhotoThumbnailJobPriority.Background;
                job.Attempts = 0;
                job.FailureCode = null;
                job.NextAttemptAtUtc = now;
                Touch(job, now);
            }

            if (stale.Count > 0)
            {
                try
                {
                    await context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // A concurrent write moved one of them; the next tick sees its new state.
                }
            }
        }
    }

    private async Task InsertOneByOneAsync(IReadOnlyList<Guid> photoIds, DateTime now, CancellationToken cancellationToken)
    {
        foreach (var photoId in photoIds)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            context.PhotoThumbnailJobs.Add(NewBackgroundJob(photoId, now));
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // The job exists (another replica), or the photo has gone: either way, nothing to do.
            }
        }
    }

    private static PhotoThumbnailJobEntry NewBackgroundJob(Guid photoId, DateTime now) => new()
    {
        PhotoId = photoId,
        State = PhotoThumbnailJobState.Pending,
        Priority = PhotoThumbnailJobPriority.Background,
        NextAttemptAtUtc = now,
        UpdatedAtUtc = now,
    };

    // ── Lease reclaim ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A running job whose lease expired most likely lost its process (§3.3 step 6): one more expiry, a <c>Crash</c>
    /// incident, and the job back to <c>Pending</c> — or, on its second expiry, <c>Unsupported (crash)</c>.
    /// </summary>
    internal async Task ReclaimExpiredAsync(CancellationToken cancellationToken)
    {
        List<Guid> expired;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var now = Now;
            expired = await scope.ServiceProvider.GetRequiredService<OdysseyContext>().PhotoThumbnailJobs
                .Where(j => j.State == PhotoThumbnailJobState.Running && j.LeaseUntilUtc < now)
                .Select(j => j.PhotoId)
                .Take(100)
                .ToListAsync(cancellationToken);
        }

        foreach (var photoId in expired)
        {
            await ReclaimAsync(photoId, cancellationToken);
        }
    }

    private async Task ReclaimAsync(Guid photoId, CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
            var now = Now;
            var job = await context.PhotoThumbnailJobs.FirstOrDefaultAsync(j => j.PhotoId == photoId, cancellationToken);
            if (job is not { State: PhotoThumbnailJobState.Running } || job.LeaseUntilUtc >= now)
            {
                return;
            }

            // The incident first, so its id can be stored on the job for the one proof-of-life give-back. If the job
            // write then loses a race, the incident is taken back before deciding again.
            var incident = new PhotoThumbnailIncident { Kind = PhotoThumbnailIncidentKind.Crash, OccurredAtUtc = now };
            context.PhotoThumbnailIncidents.Add(incident);
            await context.SaveChangesAsync(cancellationToken);

            job.LeaseExpiries++;
            job.ReclaimedRunId = job.RunId;
            job.ReclaimIncidentId = incident.Id;
            job.RunId = null;
            job.RerunRequested = false;
            job.LeaseUntilUtc = null;
            if (job.LeaseExpiries >= PhotoThumbnailTiming.MaxLeaseExpiries)
            {
                job.State = PhotoThumbnailJobState.Unsupported;
                job.FailureCode = PhotoThumbnailFailure.Crash;
            }
            else
            {
                job.State = PhotoThumbnailJobState.Pending;
                job.NextAttemptAtUtc = now;
            }

            Touch(job, now);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                tally.Crashes++;
                logger.LogWarning(
                    "Thumbnail job for photo {PhotoId} lost its lease (expiry {Expiries}); its worker most likely died mid-decode.",
                    photoId, job.LeaseExpiries);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                context.ChangeTracker.Clear();
                context.PhotoThumbnailIncidents.Remove(incident);
                await context.SaveChangesAsync(cancellationToken);
                await JitterAsync(cancellationToken);
            }
        }
    }

    // ── Claim ─────────────────────────────────────────────────────────────────────────────────────────

    internal sealed record Claimed(Guid PhotoId, Guid RunId);

    /// <summary>
    /// Takes one due job (§3.3 step 3): every 4th claim from the background lane when it has one, otherwise the
    /// interactive lane round-robin across uploaders. A claim another replica won moves on to the next candidate.
    /// </summary>
    internal async Task<Claimed?> ClaimAsync(CancellationToken cancellationToken)
    {
        claims++;
        var backgroundTurn = claims % PhotoThumbnailTiming.BackgroundEveryNthClaim == 0;
        var excluded = new HashSet<Guid>();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = backgroundTurn
                ? await NextBackgroundAsync(excluded, cancellationToken) ?? await NextInteractiveAsync(excluded, cancellationToken)
                : await NextInteractiveAsync(excluded, cancellationToken) ?? await NextBackgroundAsync(excluded, cancellationToken);
            if (candidate is not { } photoId)
            {
                return null;
            }

            if (await TryClaimAsync(photoId, cancellationToken) is { } claimed)
            {
                return claimed;
            }

            excluded.Add(photoId);
        }

        return null;
    }

    private async Task<Guid?> NextBackgroundAsync(HashSet<Guid> excluded, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var now = Now;
        return await scope.ServiceProvider.GetRequiredService<OdysseyContext>().PhotoThumbnailJobs
            .Where(j => j.State == PhotoThumbnailJobState.Pending
                && j.Priority == PhotoThumbnailJobPriority.Background
                && j.NextAttemptAtUtc <= now
                && !excluded.Contains(j.PhotoId))
            .OrderBy(j => j.NextAttemptAtUtc)
            .ThenBy(j => j.PhotoId)
            .Select(j => (Guid?)j.PhotoId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Each uploader's oldest due interactive job, then round-robin after the uploader claimed last.</summary>
    private async Task<Guid?> NextInteractiveAsync(HashSet<Guid> excluded, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = Now;
        var due = context.PhotoThumbnailJobs
            .Where(j => j.State == PhotoThumbnailJobState.Pending
                && j.Priority == PhotoThumbnailJobPriority.Interactive
                && j.NextAttemptAtUtc <= now
                && !excluded.Contains(j.PhotoId))
            .Join(context.Photos, j => j.PhotoId, p => p.PhotoId, (j, p) => new { j.PhotoId, j.NextAttemptAtUtc, Uploader = p.CreatedByUserId ?? string.Empty });

        var uploaders = await due
            .GroupBy(r => r.Uploader)
            .Select(g => new { Uploader = g.Key, Oldest = g.Min(r => r.NextAttemptAtUtc) })
            .OrderBy(g => g.Oldest)
            .Take(PhotoThumbnailTiming.MaxUploadersConsidered)
            .ToListAsync(cancellationToken);
        if (uploaders.Count == 0)
        {
            return null;
        }

        var ordered = uploaders.Select(u => u.Uploader).OrderBy(u => u, StringComparer.Ordinal).ToList();
        var next = lastUploader is null
            ? ordered[0]
            : ordered.FirstOrDefault(u => string.CompareOrdinal(u, lastUploader) > 0) ?? ordered[0];
        lastUploader = next;

        return await due
            .Where(r => r.Uploader == next)
            .OrderBy(r => r.NextAttemptAtUtc)
            .ThenBy(r => r.PhotoId)
            .Select(r => (Guid?)r.PhotoId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<Claimed?> TryClaimAsync(Guid photoId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        var now = Now;
        var job = await context.PhotoThumbnailJobs.FirstOrDefaultAsync(j => j.PhotoId == photoId, cancellationToken);
        if (job is not { State: PhotoThumbnailJobState.Pending } || job.NextAttemptAtUtc > now)
        {
            return null;
        }

        var runId = Guid.NewGuid();
        job.State = PhotoThumbnailJobState.Running;
        job.RunId = runId;
        job.LeaseUntilUtc = now + PhotoThumbnailTiming.Lease;
        job.Attempts++;
        job.AttemptedRecipeVersion = PhotoThumbnailRecipe.Version;
        job.LastClaimedAtUtc = now;
        Touch(job, now);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return new Claimed(photoId, runId);
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
    }
}
