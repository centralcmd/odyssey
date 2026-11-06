using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Core.Finance.ExchangeRateSync;
using Odyssey.Core.SystemSettings;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Jobs;

/// <summary>
/// The run log's upkeep (issue #409 §3.4, §3.6): abandons runs whose process has gone, closes held periods whose
/// process has stopped heartbeating, prunes old heartbeats, and prunes finished runs past the retention setting.
/// Run every 5 minutes by <see cref="JobRunMaintenanceJob"/>, and by the API inline for one job before a manual-run
/// insert, so a dead run never blocks a manual run longer than its stale window.
/// </summary>
/// <remarks>
/// Every write is one row's tracked save, conditional on <see cref="JobRun.Generation"/>: a worker that writes the
/// row first wins, and the sweep logs at Debug and drops its write. The prune is tracked <c>RemoveRange</c> in batches
/// of <see cref="PruneBatchSize"/>, so it runs on every test tier.
/// </remarks>
public sealed class JobRunSweeper(OdysseyContext context, IMemoryCache cache, TimeProvider time, ILogger<JobRunSweeper> logger)
{
    public const int PruneBatchSize = 1000;

    /// <summary>A heartbeat older than this, of a process with no open row, is removed.</summary>
    public static readonly TimeSpan HeartbeatRetention = TimeSpan.FromHours(24);

    private static readonly IntSettingSpec RetentionSpec = new(
        SystemSettingsKeys.JobRunRetentionDays,
        SystemSettingsDefaults.JobRunRetentionDays,
        SystemSettingsBounds.JobRunRetentionDaysMin,
        SystemSettingsBounds.JobRunRetentionDaysMax,
        ConservativeDirection.Larger);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Sweeps every job, or only <paramref name="only"/>. Returns how many rows it finished or closed.</summary>
    public async Task<int> SweepAsync(JobType? only, CancellationToken cancellationToken)
    {
        var now = Now;
        var changed = 0;
        var heartbeats = await context.JobHeartbeats.AsNoTracking().ToListAsync(cancellationToken);
        var lease = await context.FileStorageMaintenanceLeases.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == FileStorageMaintenanceLease.SingletonId, cancellationToken);

        var open = await context.JobRuns.AsNoTracking()
            .Where(r => r.FinishedAtUtc == null && (r.Status == JobRunStatus.Running || r.Status == JobRunStatus.Held))
            .Where(r => only == null || r.JobType == only)
            .Select(r => new { r.JobRunId, r.JobType, r.Status, r.Trigger, r.ClaimedAtUtc, r.StartedAtUtc, r.LastActivityAtUtc, r.WorkerId })
            .ToListAsync(cancellationToken);

        foreach (var row in open)
        {
            var entry = JobCatalogue.Get(row.JobType);
            DateTime? finishAt = null;
            var close = false;
            if (row.Status == JobRunStatus.Running)
            {
                if (row.Trigger == JobRunTrigger.Manual && row.ClaimedAtUtc is null
                    && row.StartedAtUtc < now - JobCatalogue.UnclaimedRequestTimeout)
                {
                    // Requested, but no worker took it (none running, or all down).
                    finishAt = row.StartedAtUtc + JobCatalogue.UnclaimedRequestTimeout;
                    logger.LogWarning("{JobType} run {RunId} was requested but no worker took it; it is marked failed. Is Odyssey.Worker running?",
                        row.JobType, row.JobRunId);
                }
                else if (row.LastActivityAtUtc < now - entry.StaleAfter && LeaseAllowsSweep(row.JobType, lease, now))
                {
                    // Stamped at the latest it can have been alive, not at discovery.
                    finishAt = row.JobType == JobType.ExchangeRateSync
                        ? row.StartedAtUtc.AddMinutes(ExchangeRateSyncConstants.RunTimeoutMinutes)
                        : row.LastActivityAtUtc;
                    logger.LogWarning("{JobType} run {RunId} was abandoned mid-run and is marked failed.", row.JobType, row.JobRunId);
                }
            }
            else if (!IsLive(heartbeats, row.JobType, row.WorkerId, entry, now))
            {
                // A held period whose process has stopped heartbeating: closed at its last sign of life (S11).
                close = true;
                finishAt = heartbeats
                    .Where(h => h.JobType == row.JobType && h.WorkerId == row.WorkerId)
                    .Select(h => (DateTime?)h.LastTickAtUtc)
                    .FirstOrDefault() ?? row.StartedAtUtc;
            }

            if (finishAt is not { } at)
            {
                continue;
            }

            if (await FinishOneAsync(row.JobRunId, close, at, cancellationToken))
            {
                changed++;
            }
        }

        if (only is null)
        {
            await PruneHeartbeatsAsync(heartbeats, now, cancellationToken);
        }

        return changed;
    }

    /// <summary>Whether <paramref name="workerId"/> has heartbeaten <paramref name="jobType"/> within its not-ticking window.</summary>
    public static bool IsLive(IEnumerable<JobHeartbeat> heartbeats, JobType jobType, Guid? workerId, JobCatalogueEntry entry, DateTime now) =>
        workerId is { } id && heartbeats.Any(h => h.JobType == jobType && h.WorkerId == id
            && h.LastTickAtUtc >= now - TimeSpan.FromSeconds(entry.NotTickingAfterSeconds));

    /// <summary>
    /// The file-storage pass holds a lease it renews every minute: the sweep acts only once that lease has expired, so
    /// it can never free the guard under a live pass (S4).
    /// </summary>
    private static bool LeaseAllowsSweep(JobType jobType, FileStorageMaintenanceLease? lease, DateTime now) =>
        jobType != JobType.FileStorageMaintenance || lease?.LeaseUntilUtc is not { } until || until <= now;

    private async Task<bool> FinishOneAsync(Guid runId, bool closeHeld, DateTime at, CancellationToken cancellationToken)
    {
        try
        {
            var run = await context.JobRuns.FirstOrDefaultAsync(r => r.JobRunId == runId, cancellationToken);
            if (run is null || !run.IsOpen)
            {
                return false;
            }

            if (closeHeld)
            {
                run.Finish(JobRunStatus.Held, null, at);
            }
            else
            {
                if (run is ExchangeRateSyncJobRun sync)
                {
                    sync.ErrorKind = ExchangeRateSyncErrorKind.Abandoned;
                }

                run.Finish(JobRunStatus.Failed, JobFailureCategory.Abandoned, at);
            }

            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The process wrote the row first: it is live after all, and its write wins.
            logger.LogDebug("Run {RunId} was written by its worker while the sweep acted on it; the sweep's write is dropped.", runId);
            return false;
        }
        finally
        {
            context.ChangeTracker.Clear();
        }
    }

    private async Task PruneHeartbeatsAsync(IReadOnlyCollection<JobHeartbeat> heartbeats, DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - HeartbeatRetention;
        var old = heartbeats.Where(h => h.LastTickAtUtc < cutoff).ToList();
        if (old.Count == 0)
        {
            return;
        }

        var workers = old.Select(h => h.WorkerId).Distinct().ToList();
        var withOpenRows = await context.JobRuns.AsNoTracking()
            .Where(r => r.FinishedAtUtc == null && r.WorkerId != null && workers.Contains(r.WorkerId.Value))
            .Select(r => new { r.JobType, r.WorkerId })
            .ToListAsync(cancellationToken);
        var removable = old.Where(h => !withOpenRows.Any(r => r.JobType == h.JobType && r.WorkerId == h.WorkerId)).ToList();
        if (removable.Count == 0)
        {
            return;
        }

        context.JobHeartbeats.RemoveRange(removable);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A worker came back and wrote its heartbeat meanwhile, or another sweep removed it.
        }
        finally
        {
            context.ChangeTracker.Clear();
        }
    }

    // ── Retention (§3.6) ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The retention in days. A degraded read resolves to the larger value: never prune more on a fault (A18).</summary>
    public async Task<int> ResolveRetentionDaysAsync(CancellationToken cancellationToken)
    {
        var resolver = new IntSettingResolver(cache, logger, "job-run-retention");
        var (values, readFailed) = await resolver.ReadAsync(context, [RetentionSpec.Key], "background-job run retention", cancellationToken);
        return resolver.Resolve(RetentionSpec, values, readFailed).Value;
    }

    /// <summary>
    /// Deletes finished runs that started before the retention window, in batches of <see cref="PruneBatchSize"/>.
    /// A <c>Running</c> row and an open held period are never pruned. Returns how many rows went.
    /// </summary>
    public async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var days = await ResolveRetentionDaysAsync(cancellationToken);
        var cutoff = Now.AddDays(-days);
        var total = 0;
        while (true)
        {
            var batch = await context.JobRuns
                .Where(r => r.FinishedAtUtc != null && r.StartedAtUtc < cutoff)
                .OrderBy(r => r.StartedAtUtc)
                .Take(PruneBatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            context.JobRuns.RemoveRange(batch);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                total += batch.Count;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another replica pruned some of them; the next batch reads afresh.
            }
            finally
            {
                context.ChangeTracker.Clear();
            }

            if (batch.Count < PruneBatchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            logger.LogInformation("Pruned {Count} background-job runs older than {Days} days.", total, days);
        }

        return total;
    }
}

/// <summary>
/// The run log's upkeep job (issue #409 §3.4, §3.6), run by <c>Odyssey.Worker</c> every 5 minutes: the stale-run
/// sweep on every run, the retention prune once an hour. It records no runs of its own — the catalogue declares it
/// infrastructure.
/// </summary>
public sealed class JobRunMaintenanceJob(IServiceScopeFactory scopes, TimeProvider time) : IPeriodicJob
{
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan PruneEvery = TimeSpan.FromHours(1);

    private DateTime? lastPrune;

    public string Name => "job-run-maintenance";

    public TimeSpan Interval => Tick;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobRunSweeper>().SweepAsync(null, cancellationToken);
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (lastPrune is { } last && now - last < PruneEvery)
        {
            return;
        }

        await using (var scope = scopes.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobRunSweeper>().PruneAsync(cancellationToken);
        }

        lastPrune = now;
    }
}
