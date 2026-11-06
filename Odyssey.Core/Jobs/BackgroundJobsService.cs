using Microsoft.EntityFrameworkCore;
using Odyssey.Context;
using Odyssey.Core.FileStorage;
using Odyssey.Core.Finance.ExchangeRateSync;
using Odyssey.Core.Journal;
using Odyssey.Core.Journal.Thumbnails;
using Odyssey.Core.Weather;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Jobs;

/// <summary>
/// What the caller may see and do, per job (issue #409 §3.1, §5): resolved from the caller's claims by the controller,
/// because a domain service holds no <c>ClaimsPrincipal</c>.
/// </summary>
/// <param name="DetailReadable">Jobs whose detail-read claim the caller holds; every other job's own fields are <c>null</c>.</param>
/// <param name="Runnable">Jobs for which the caller holds every manual-run claim.</param>
public sealed record BackgroundJobAccess(IReadOnlySet<JobType> DetailReadable, IReadOnlySet<JobType> Runnable)
{
    public bool CanRead(JobType jobType) => DetailReadable.Contains(jobType);
}

/// <summary>The outcome of a manual-run request (§5.4), which the controller turns into its status code.</summary>
public abstract record ManualRunOutcome
{
    private ManualRunOutcome()
    {
    }

    /// <summary><c>202</c>: the unclaimed row is the hand-off.</summary>
    public sealed record Accepted(Guid RunId) : ManualRunOutcome;

    /// <summary><c>200</c>: nothing to do, finished <c>Skipped</c> at once.</summary>
    public sealed record Skipped(ExistingJobRun Run) : ManualRunOutcome;

    /// <summary><c>429</c>: within the job's cooldown.</summary>
    public sealed record Cooldown(DateTime AvailableAtUtc) : ManualRunOutcome;

    /// <summary><c>409 job-run.already-running</c>: the job's guard is held.</summary>
    public sealed record AlreadyRunning : ManualRunOutcome;

    /// <summary><c>409 job-run.held</c>: the job is held, so a manual run could not start.</summary>
    public sealed record Held(JobHeldReason Reason, DateTime? ResumesAtUtc) : ManualRunOutcome;
}

/// <summary>
/// The background-jobs page's read model and its manual run (issue #409 §5): the per-job overview, the filtered run
/// list, the polymorphic detail, and starting a manual run of any job. Every field derived from a job's own domain
/// follows that job's detail-read claim and is <c>null</c> without it (A1/S5); no DTO carries a user id, a worker id,
/// a name, a path or exception text (§7.2).
/// </summary>
public sealed class BackgroundJobsService(
    OdysseyContext context,
    ExchangeRateSyncService exchangeRateSync,
    IWeatherSettingsLookup weatherSettings,
    FileStorageVolumeRegistry volumes,
    PhotoThumbnailService thumbnails,
    JobRunSweeper sweeper,
    TimeProvider time)
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private static readonly ThumbnailWorkerState[] HeldThumbnailStates =
        [ThumbnailWorkerState.Paused, ThumbnailWorkerState.Tripped, ThumbnailWorkerState.PolicyFailed, ThumbnailWorkerState.StorageUnavailable];

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // ── Overview (§5.1) ──────────────────────────────────────────────────────────────────────────────────

    public async Task<BackgroundJobsOverview> GetOverviewAsync(BackgroundJobAccess access, CancellationToken cancellationToken)
    {
        var now = Now;
        var heartbeats = await context.JobHeartbeats.AsNoTracking().ToListAsync(cancellationToken);
        var weekAgo = now - Week;
        var recent = await context.JobRuns.AsNoTracking()
            .Where(r => r.StartedAtUtc >= weekAgo)
            .Select(r => new RunFacts(r.JobType, r.Status, r.StartedAtUtc, r.FinishedAtUtc))
            .ToListAsync(cancellationToken);
        var open = await context.JobRuns.AsNoTracking()
            .Where(r => r.FinishedAtUtc == null && (r.Status == JobRunStatus.Running || r.Status == JobRunStatus.Held))
            .OrderBy(r => r.StartedAtUtc)
            .Select(r => new OpenRun(r.JobRunId, r.JobType, r.Trigger, r.Status, r.HeldReason, r.StartedAtUtc, r.ClaimedAtUtc, r.LastActivityAtUtc, r.WorkerId))
            .ToListAsync(cancellationToken);
        var heldChecks = await context.Set<FileStorageMaintenanceJobRun>().AsNoTracking()
            .Where(r => r.Status == JobRunStatus.Held && r.FinishedAtUtc == null)
            .Select(r => new { r.JobRunId, r.HeldByCheck })
            .ToDictionaryAsync(r => r.JobRunId, r => r.HeldByCheck, cancellationToken);

        var overview = new BackgroundJobsOverview { GeneratedAtUtc = now };
        foreach (var entry in JobCatalogue.Entries)
        {
            var job = await SummarizeAsync(entry, access, heartbeats, recent, open, heldChecks, now, cancellationToken);
            overview.Jobs.Add(job);
        }

        overview.Totals = Totals(recent, now);
        overview.Running = open
            .Where(r => r.Status == JobRunStatus.Running && IsLiveRunning(r, heartbeats, now))
            .Select(r => new RunningJobRun { Id = r.JobRunId, JobType = r.JobType, Trigger = r.Trigger, StartedAtUtc = r.StartedAtUtc })
            .ToList();
        overview.WorkerHealth = new BackgroundJobsWorkerHealth
        {
            FileStorage = access.CanRead(JobType.FileStorageMaintenance) ? await FileStorageHealthAsync(now, cancellationToken) : null,
            Thumbnails = access.CanRead(JobType.PhotoThumbnails) ? await ThumbnailHealthAsync(now, cancellationToken) : null,
        };
        return overview;
    }

    private async Task<BackgroundJobSummary> SummarizeAsync(
        JobCatalogueEntry entry,
        BackgroundJobAccess access,
        IReadOnlyList<JobHeartbeat> heartbeats,
        IReadOnlyList<RunFacts> recent,
        IReadOnlyList<OpenRun> open,
        IReadOnlyDictionary<Guid, string?> heldChecks,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var jobType = entry.JobType;
        var canRead = access.CanRead(jobType);
        var runs = context.JobRuns.AsNoTracking().Where(r => r.JobType == jobType);
        var lastRun = await runs
            .Where(r => r.FinishedAtUtc != null && r.Status != JobRunStatus.Held)
            .OrderByDescending(r => r.FinishedAtUtc).ThenByDescending(r => r.StartedAtUtc)
            .Select(r => new JobRunReference { Id = r.JobRunId, Status = r.Status, StartedAtUtc = r.StartedAtUtc, FinishedAtUtc = r.FinishedAtUtc })
            .FirstOrDefaultAsync(cancellationToken);
        var lastSuccess = await runs
            .Where(r => r.Status == JobRunStatus.Succeeded || r.Status == JobRunStatus.PartiallySucceeded)
            .MaxAsync(r => r.FinishedAtUtc, cancellationToken);
        var lastDecisive = await runs
            .Where(r => r.FinishedAtUtc != null && r.Status != JobRunStatus.Held && r.Status != JobRunStatus.Skipped)
            .OrderByDescending(r => r.FinishedAtUtc).ThenByDescending(r => r.StartedAtUtc)
            .Select(r => (JobRunStatus?)r.Status)
            .FirstOrDefaultAsync(cancellationToken);
        var lastManual = await runs.Where(r => r.Trigger == JobRunTrigger.Manual).MaxAsync(r => (DateTime?)r.StartedAtUtc, cancellationToken);

        var jobHeartbeats = heartbeats.Where(h => h.JobType == jobType).ToList();
        var lastHeartbeat = jobHeartbeats.Count == 0 ? (DateTime?)null : jobHeartbeats.Max(h => h.LastTickAtUtc);
        var ticking = lastHeartbeat is { } beat && beat >= now - TimeSpan.FromSeconds(entry.NotTickingAfterSeconds);
        var openRows = open.Where(r => r.JobType == jobType).ToList();
        var liveHeld = openRows.FirstOrDefault(r => r.Status == JobRunStatus.Held
            && JobRunSweeper.IsLive(heartbeats, jobType, r.WorkerId, entry, now));
        var liveRunning = openRows.Any(r => r.Status == JobRunStatus.Running && IsLiveRunning(r, heartbeats, now));

        var (notConfigured, nextExpected, cadence) = await ScheduleAsync(entry, now, cancellationToken);
        int? overdueAfter = cadence is { } every ? (int)(2 * every).TotalSeconds : null;

        DateTime? heldUntil = null;
        if (canRead && liveHeld?.HeldReason == JobHeldReason.BreakerTripped)
        {
            heldUntil = await PhotoThumbnailBreaker.ResumesAtAsync(context, now, cancellationToken);
        }

        var health =
            !ticking ? JobHealth.NotTicking
            : liveHeld is not null ? JobHealth.Held
            : liveRunning ? JobHealth.Running
            : notConfigured ? JobHealth.NotConfigured
            : lastDecisive == JobRunStatus.Failed ? JobHealth.Failing
            : overdueAfter is { } overdue && lastRun?.FinishedAtUtc is { } finished && finished < now.AddSeconds(-overdue) ? JobHealth.Overdue
            : lastRun is null ? JobHealth.NeverRun
            : JobHealth.Healthy;

        var cooldownEnds = lastManual + JobCatalogue.ManualRunCooldown;
        return new BackgroundJobSummary
        {
            JobType = jobType,
            Health = health,
            LastRun = lastRun,
            LastSuccessAtUtc = lastSuccess,
            LastHeartbeatAtUtc = lastHeartbeat,
            NextExpectedRunAtUtc = nextExpected,
            HeldReason = canRead ? liveHeld?.HeldReason : null,
            HeldCheck = canRead && liveHeld is not null ? heldChecks.GetValueOrDefault(liveHeld.JobRunId) : null,
            HeldUntilUtc = heldUntil,
            CanRunNow = access.Runnable.Contains(jobType),
            CanReadDetail = canRead,
            ManualRunAvailableAtUtc = cooldownEnds > now ? cooldownEnds : null,
            NotTickingAfterSeconds = entry.NotTickingAfterSeconds,
            OverdueAfterSeconds = overdueAfter,
            Totals = Totals(recent.Where(r => r.JobType == jobType).ToList(), now),
        };
    }

    /// <summary>Whether the job has anything to work on, when its next scheduled run is due, and its cadence.</summary>
    private async Task<(bool NotConfigured, DateTime? NextExpected, TimeSpan? Cadence)> ScheduleAsync(
        JobCatalogueEntry entry, DateTime now, CancellationToken cancellationToken)
    {
        switch (entry.JobType)
        {
            case JobType.ExchangeRateSync:
                var status = await exchangeRateSync.GetStatusAsync(cancellationToken);
                return (status.EnabledCount == 0, status.NextRunAtUtc, TimeSpan.FromHours(status.IntervalHours));
            case JobType.Weather:
                return (!await WeatherHasWorkAsync(cancellationToken), null, null);
            case JobType.FileStorageMaintenance:
                var last = await context.FileStorageMaintenanceLeases.AsNoTracking()
                    .Where(l => l.Id == FileStorageMaintenanceLease.SingletonId)
                    .Select(l => l.LastCompletedAtUtc)
                    .FirstOrDefaultAsync(cancellationToken);
                var configured = volumes.IsActive;
                DateTime? next = !configured ? null : last is { } completed ? completed + FileStorageMaintenanceJob.PassInterval : now;
                return (!configured, next, entry.Cadence);
            default:
                // Thumbnails are never NotConfigured: a photo can be uploaded at any time.
                return (false, null, entry.Cadence);
        }
    }

    private async Task<bool> WeatherHasWorkAsync(CancellationToken cancellationToken)
    {
        var settings = await weatherSettings.GetAsync(cancellationToken);
        return settings.IsConfigured && await context.WeatherPoints.AsNoTracking()
            .AnyAsync(p => (p.ForecastEnabled || p.HistoryEnabled) && p.PurgeStartedAt == null, cancellationToken);
    }

    private async Task<FileStorageWorkerHealth> FileStorageHealthAsync(DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - FileStorageWorkerVerification.VerdictTtl;
        var verdicts = await context.FileStorageWorkerVerdicts.AsNoTracking()
            .Where(v => v.LastReportedAtUtc >= cutoff)
            .Select(v => v.FailedCheck)
            .ToListAsync(cancellationToken);
        return new FileStorageWorkerHealth
        {
            FailingWorkers = verdicts.Count,
            FailingChecks = verdicts.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
        };
    }

    private async Task<ThumbnailWorkerHealth> ThumbnailHealthAsync(DateTime now, CancellationToken cancellationToken)
    {
        var state = await thumbnails.GetWorkerStateAsync(cancellationToken);
        return new ThumbnailWorkerHealth
        {
            State = state,
            ResumesAtUtc = state == ThumbnailWorkerState.Tripped
                ? await PhotoThumbnailBreaker.ResumesAtAsync(context, now, cancellationToken)
                : null,
        };
    }

    /// <summary>
    /// A <c>Running</c> row counts only while its process heartbeats (§3.4): a crashed worker's row never reads as a
    /// live run. A row no worker has taken counts while the request is fresh; a row the API runs itself (a purge)
    /// names no worker and counts while it is within its stale window.
    /// </summary>
    private static bool IsLiveRunning(OpenRun run, IReadOnlyList<JobHeartbeat> heartbeats, DateTime now)
    {
        var entry = JobCatalogue.Get(run.JobType);
        if (run.WorkerId is { } workerId)
        {
            return JobRunSweeper.IsLive(heartbeats, run.JobType, workerId, entry, now);
        }

        return run.ClaimedAtUtc is null
            ? run.StartedAtUtc >= now - JobCatalogue.UnclaimedRequestTimeout
            : run.LastActivityAtUtc >= now - entry.StaleAfter;
    }

    private static JobRunTotals Totals(IReadOnlyCollection<RunFacts> runs, DateTime now) => new()
    {
        Last24h = Window(runs.Where(r => r.StartedAtUtc >= now - Day)),
        Last7d = Window(runs),
    };

    private static JobRunTotalsWindow Window(IEnumerable<RunFacts> runs)
    {
        var window = new JobRunTotalsWindow();
        foreach (var run in runs)
        {
            if (run.Status == JobRunStatus.Held)
            {
                window.Held++;
                continue;
            }

            if (run.FinishedAtUtc is null)
            {
                continue;
            }

            window.Runs++;
            if (run.Status is JobRunStatus.Succeeded or JobRunStatus.PartiallySucceeded)
            {
                window.Succeeded++;
            }
            else if (run.Status == JobRunStatus.Failed)
            {
                window.Failed++;
            }
        }

        return window;
    }

    // ── The run list (§5.2) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One page of the run log. The date filter is calendar days in the caller's time zone: <c>from</c> is that day's
    /// local midnight and <c>to</c> the next local midnight after it, exclusive. A zone the host cannot resolve is a
    /// generic <c>400</c> keyed <c>tz</c> that echoes nothing (S-N3).
    /// </summary>
    public async Task<PagedResult<ExistingJobRun>> ListRunsAsync(
        JobRunsQueryParams query, BackgroundJobAccess access, CancellationToken cancellationToken)
    {
        var runs = context.JobRuns.AsNoTracking();
        if (query.JobTypes is { Length: > 0 } types)
        {
            runs = runs.Where(r => types.Contains(r.JobType));
        }

        if (query.Statuses is { Length: > 0 } statuses)
        {
            runs = runs.Where(r => statuses.Contains(r.Status));
        }

        if (query.Triggers is { Length: > 0 } triggers)
        {
            runs = runs.Where(r => triggers.Contains(r.Trigger));
        }

        if (query.StartedFrom is not null || query.StartedTo is not null)
        {
            var zone = ResolveZone(query.Tz);
            if (query.StartedFrom is { } from)
            {
                var fromUtc = LocalMidnightUtc(from, zone);
                runs = runs.Where(r => r.StartedAtUtc >= fromUtc);
            }

            if (query.StartedTo is { } to)
            {
                var toUtc = LocalMidnightUtc(to.AddDays(1), zone);
                runs = runs.Where(r => r.StartedAtUtc < toUtc);
            }
        }

        var ascending = query.SortDir == SortDirection.Asc;
        var ordered = (query.SortBy ?? JobRunSortBy.StartedAt) switch
        {
            // A run without a duration (running, or an open held period) is last in both directions.
            JobRunSortBy.Duration => ascending
                ? runs.OrderBy(r => r.DurationMs == null).ThenBy(r => r.DurationMs)
                : runs.OrderBy(r => r.DurationMs == null).ThenByDescending(r => r.DurationMs),
            JobRunSortBy.JobType => ascending ? runs.OrderBy(r => r.JobType) : runs.OrderByDescending(r => r.JobType),
            JobRunSortBy.Status => ascending ? runs.OrderBy(r => r.Status) : runs.OrderByDescending(r => r.Status),
            _ => ascending ? runs.OrderBy(r => r.StartedAtUtc) : runs.OrderByDescending(r => r.StartedAtUtc),
        };
        // Every sort tiebreaks on the newest start, then the id.
        ordered = ordered.ThenByDescending(r => r.StartedAtUtc).ThenBy(r => r.JobRunId);

        var total = await ordered.CountAsync(cancellationToken);
        var offset = Math.Clamp(query.Offset, 0, JobRunsQueryParams.MaxOffset);
        var limit = Math.Clamp(query.Limit, 1, JobRunsQueryParams.MaxLimit);
        var page = await ordered.Skip(offset).Take(limit).ToListAsync(cancellationToken);
        return new PagedResult<ExistingJobRun>
        {
            Items = page.Select(run => ToExisting(run, access.CanRead(run.JobType))).ToList(),
            Offset = offset,
            Limit = limit,
            TotalCount = total,
        };
    }

    /// <summary>The shape allow-list ran in model validation; this is the lookup, failing the same generic way.</summary>
    internal static TimeZoneInfo ResolveZone(string? tz)
    {
        if (string.IsNullOrEmpty(tz) || tz.Length > JobRunsQueryParams.TimeZoneMaxLength
            || !System.Text.RegularExpressions.Regex.IsMatch(tz, JobRunsQueryParams.TimeZonePattern)
            || !TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var zone))
        {
            throw new DomainValidationException(JobRunsQueryParams.TimeZoneMessage, code: null, field: "tz");
        }

        return zone;
    }

    /// <summary>
    /// The UTC instant of <paramref name="date"/>'s local midnight. A midnight a DST change skips resolves to the
    /// first valid local instant after it; an ambiguous one to its earlier occurrence.
    /// </summary>
    internal static DateTime LocalMidnightUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(15);
        }

        if (zone.IsAmbiguousTime(local))
        {
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    // ── The detail (§5.3) ────────────────────────────────────────────────────────────────────────────────

    public async Task<JobRunDetail?> GetRunAsync(Guid id, BackgroundJobAccess access, CancellationToken cancellationToken)
    {
        var run = await context.JobRuns.AsNoTracking().FirstOrDefaultAsync(r => r.JobRunId == id, cancellationToken);
        return run is null ? null : ToDetail(run, access.CanRead(run.JobType));
    }

    /// <summary>A list row; the job's own fields are <c>null</c> without its detail-read claim, never <c>0</c>.</summary>
    public static ExistingJobRun ToExisting(JobRun run, bool canRead) => new()
    {
        Id = run.JobRunId,
        JobType = run.JobType,
        Trigger = run.Trigger,
        Status = run.Status,
        FailureCategory = run.FailureCategory,
        HeldReason = canRead ? run.HeldReason : null,
        StartedAtUtc = run.StartedAtUtc,
        FinishedAtUtc = run.FinishedAtUtc,
        DurationMs = run.DurationMs,
        ItemsProcessed = canRead ? run.ItemsProcessed : null,
        ItemsFailed = canRead ? run.ItemsFailed : null,
    };

    public static JobRunDetail ToDetail(JobRun run, bool canRead)
    {
        JobRunDetail detail = run switch
        {
            ExchangeRateSyncJobRun sync when canRead => new ExchangeRateSyncJobRunDetail
            {
                ErrorKind = sync.ErrorKind,
                SpanFrom = sync.SpanFrom,
                SpanTo = sync.SpanTo,
                CurrencyCodes = sync.CurrencyCodes.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                RowsInserted = sync.RowsInserted,
                RowsSkipped = sync.RowsSkipped,
                RowsRejected = sync.RowsRejected,
                RowsDeleted = sync.RowsDeleted,
            },
            ExchangeRateSyncJobRun => new ExchangeRateSyncJobRunDetail(),
            WeatherJobRun weather when canRead => new WeatherJobRunDetail
            {
                PointsAttempted = weather.PointsAttempted,
                PointsSkippedNotExpired = weather.PointsSkippedNotExpired,
                PointsFailed = weather.PointsFailed,
                ForecastsStored = weather.ForecastsStored,
                ObservationDaysStored = weather.ObservationDaysStored,
                FailureKind = weather.FailureKind,
            },
            WeatherJobRun => new WeatherJobRunDetail(),
            FileStorageMaintenanceJobRun storage when canRead => new FileStorageMaintenanceJobRunDetail
            {
                FilesErased = storage.FilesErased,
                AbandonedWritesSwept = storage.AbandonedWritesSwept,
                ObjectsScrubbed = storage.ObjectsScrubbed,
                IntegrityFailures = storage.IntegrityFailures,
                OrphansFound = storage.OrphansFound,
                HeldByCheck = storage.HeldByCheck,
            },
            FileStorageMaintenanceJobRun => new FileStorageMaintenanceJobRunDetail(),
            PhotoThumbnailJobRun photos when canRead => new PhotoThumbnailJobRunDetail
            {
                PhotosRendered = photos.PhotosRendered,
                PhotosFailed = photos.PhotosFailed,
                PhotosRequeued = photos.PhotosRequeued,
                Crashes = photos.Crashes,
                Hangs = photos.Hangs,
            },
            PhotoThumbnailJobRun => new PhotoThumbnailJobRunDetail(),
            _ => new JobRunDetail(),
        };

        detail.Id = run.JobRunId;
        detail.JobType = run.JobType;
        detail.Trigger = run.Trigger;
        detail.Status = run.Status;
        detail.FailureCategory = run.FailureCategory;
        detail.HeldReason = canRead ? run.HeldReason : null;
        detail.StartedAtUtc = run.StartedAtUtc;
        detail.FinishedAtUtc = run.FinishedAtUtc;
        detail.ClaimedAtUtc = run.ClaimedAtUtc;
        detail.LastActivityAtUtc = run.LastActivityAtUtc;
        detail.DurationMs = run.DurationMs;
        detail.ItemsProcessed = canRead ? run.ItemsProcessed : null;
        detail.ItemsFailed = canRead ? run.ItemsFailed : null;
        return detail;
    }

    // ── Manual run (§3.5, §5.4) ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a manual run of <paramref name="entry"/>'s job. The caller has already passed every claim the job
    /// lists. In order: the job's cooldown, its held state (thumbnails), the inline sweep of its stale rows, nothing
    /// to do (a finished <c>Skipped</c> row), the guard, and the unclaimed <c>Running</c> row a worker picks up.
    /// </summary>
    public async Task<ManualRunOutcome> StartManualAsync(
        JobCatalogueEntry entry, string userId, bool canReadDetail, CancellationToken cancellationToken)
    {
        if (entry.JobType == JobType.ExchangeRateSync)
        {
            return await StartExchangeRateSyncAsync(userId, cancellationToken);
        }

        var now = Now;
        var jobType = entry.JobType;
        var lastManual = await context.JobRuns.AsNoTracking()
            .Where(r => r.JobType == jobType && r.Trigger == JobRunTrigger.Manual)
            .MaxAsync(r => (DateTime?)r.StartedAtUtc, cancellationToken);
        if (lastManual is { } last && last + JobCatalogue.ManualRunCooldown > now)
        {
            return new ManualRunOutcome.Cooldown(last + JobCatalogue.ManualRunCooldown);
        }

        if (jobType == JobType.PhotoThumbnails)
        {
            var state = await thumbnails.GetWorkerStateAsync(cancellationToken);
            if (HeldThumbnailStates.Contains(state))
            {
                var reason = PhotoThumbnailJob.HeldReasonOf(state)!.Value;
                var resumes = state == ThumbnailWorkerState.Tripped
                    ? await PhotoThumbnailBreaker.ResumesAtAsync(context, now, cancellationToken)
                    : null;
                return new ManualRunOutcome.Held(reason, canReadDetail ? resumes : null);
            }
        }

        // A dead run must never block a manual run for longer than its stale window (§3.4).
        await sweeper.SweepAsync(jobType, cancellationToken);

        if (!await HasWorkAsync(jobType, cancellationToken))
        {
            var skipped = NewManualRun(jobType, userId, now);
            skipped.ClaimedAtUtc = now;
            skipped.Finish(JobRunStatus.Skipped, null, now);
            context.JobRuns.Add(skipped);
            await context.SaveChangesAsync(cancellationToken);
            return new ManualRunOutcome.Skipped(ToExisting(skipped, canReadDetail));
        }

        // The application's pre-check; on MariaDB the ExclusiveGuard unique index is the real guard.
        var exclusive = jobType == JobType.FileStorageMaintenance;
        if (await context.JobRuns.AnyAsync(r => r.JobType == jobType && r.Status == JobRunStatus.Running
                && (exclusive || r.Trigger == JobRunTrigger.Manual), cancellationToken))
        {
            return new ManualRunOutcome.AlreadyRunning();
        }

        var run = NewManualRun(jobType, userId, now);
        context.JobRuns.Add(run);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DbErrors.IsDuplicateKey(exception))
        {
            context.Entry(run).State = EntityState.Detached;
            return new ManualRunOutcome.AlreadyRunning();
        }

        return new ManualRunOutcome.Accepted(run.JobRunId);
    }

    private async Task<ManualRunOutcome> StartExchangeRateSyncAsync(string userId, CancellationToken cancellationToken)
    {
        ExchangeRateSyncJobRun run;
        try
        {
            run = await exchangeRateSync.StartManualAsync(userId, cancellationToken);
        }
        catch (ExchangeRateSyncCooldownException cooldown)
        {
            return new ManualRunOutcome.Cooldown(cooldown.AvailableAtUtc);
        }
        catch (DomainConflictException)
        {
            return new ManualRunOutcome.AlreadyRunning();
        }

        return run.Status == JobRunStatus.Running
            ? new ManualRunOutcome.Accepted(run.JobRunId)
            : new ManualRunOutcome.Skipped(ToExisting(run, canRead: true));
    }

    /// <summary>Whether a manual run would find anything to do; one that would not is finished <c>Skipped</c> at once.</summary>
    private async Task<bool> HasWorkAsync(JobType jobType, CancellationToken cancellationToken)
    {
        switch (jobType)
        {
            case JobType.Weather:
                var settings = await weatherSettings.GetAsync(cancellationToken);
                return settings.IsConfigured && await WeatherPointService.TrackedQueue(context).AnyAsync(cancellationToken);
            case JobType.FileStorageMaintenance:
                return volumes.IsActive;
            case JobType.PhotoThumbnails:
                return await context.PhotoThumbnailJobs.AnyAsync(j => j.State == PhotoThumbnailJobState.Pending
                        || (j.State == PhotoThumbnailJobState.Failed && j.ManualRequeues == 0), cancellationToken)
                    || await context.Photos.AnyAsync(p => !context.PhotoThumbnailJobs.Any(j => j.PhotoId == p.PhotoId), cancellationToken);
            default:
                return true;
        }
    }

    private static JobRun NewManualRun(JobType jobType, string userId, DateTime now)
    {
        JobRun run = jobType switch
        {
            JobType.Weather => new WeatherJobRun(),
            JobType.FileStorageMaintenance => new FileStorageMaintenanceJobRun(),
            JobType.PhotoThumbnails => new PhotoThumbnailJobRun(),
            _ => throw new ArgumentOutOfRangeException(nameof(jobType)),
        };
        run.JobRunId = Guid.NewGuid();
        run.Trigger = JobRunTrigger.Manual;
        run.Status = JobRunStatus.Running;
        run.StartedAtUtc = now;
        run.LastActivityAtUtc = now;
        run.RequestedByUserId = userId;
        return run;
    }

    private sealed record RunFacts(JobType JobType, JobRunStatus Status, DateTime StartedAtUtc, DateTime? FinishedAtUtc);

    private sealed record OpenRun(
        Guid JobRunId,
        JobType JobType,
        JobRunTrigger Trigger,
        JobRunStatus Status,
        JobHeldReason? HeldReason,
        DateTime StartedAtUtc,
        DateTime? ClaimedAtUtc,
        DateTime LastActivityAtUtc,
        Guid? WorkerId);
}
