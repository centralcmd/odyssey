using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;

namespace Odyssey.Context;

// The shared background-job run log (issue #409 §4). One table, TPH on JobType: the common columns on the base, each
// job's own counters on its subtype as nullable columns. Operational data — out of scope for every export.

/// <summary>
/// One run of a background job: a scheduled run, a manual run, a work burst (weather and thumbnails, §3.2), or a
/// <c>Held</c> period during which one process could not run its job (§3.3 step 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two guards are generated columns.</b> <see cref="ExclusiveGuard"/> holds the job type while a run that must be
/// exclusive is <c>Running</c> — every exchange-rate sync and file-storage run, and any manual run — so its unique
/// index makes a second one fail on insert. <see cref="OpenHeldGuard"/> is <c>1</c> on an open <c>Held</c> row, and
/// unique with <c>(JobType, WorkerId)</c>, so one process opens one held period per job. Only real MariaDB enforces
/// either; the EF InMemory tiers rely on the application's pre-checks.
/// </para>
/// <para>
/// <b><see cref="Generation"/> is the concurrency token, and every write bumps it</b> — <c>OdysseyContext</c>
/// rotates it on each save of a modified row, so no writer can forget. The worker and the stale-run sweep therefore
/// never both win: whichever saves second gets a concurrency failure and drops its write (§3.4).
/// </para>
/// </remarks>
[Table("JobRuns")]
[Index(nameof(JobType), nameof(StartedAtUtc))]
[Index(nameof(StartedAtUtc))]
[Index(nameof(Status), nameof(StartedAtUtc))]
[Index(nameof(DurationMs))]
[Index(nameof(ExclusiveGuard), IsUnique = true)]
[Index(nameof(JobType), nameof(WorkerId), nameof(OpenHeldGuard), IsUnique = true)]
public abstract class JobRun
{
    protected JobRun(JobType jobType)
    {
        JobType = jobType;
    }

    [Key]
    public Guid JobRunId { get; set; }

    /// <summary>The TPH discriminator.</summary>
    public JobType JobType { get; private set; }

    public JobRunTrigger Trigger { get; set; }

    public JobRunStatus Status { get; set; }

    /// <summary><c>NULL</c> on success and on a held period.</summary>
    public JobFailureCategory? FailureCategory { get; set; }

    /// <summary>Set only on a <c>Held</c> row.</summary>
    public JobHeldReason? HeldReason { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>Written when the run finishes.</summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// When the executing process took the run. A scheduled run, a burst and a purge are claimed by whoever inserts
    /// them; a manual run is inserted unclaimed by the API and the first worker to set this (through
    /// <see cref="Generation"/>) executes it.
    /// </summary>
    public DateTime? ClaimedAtUtc { get; set; }

    /// <summary>The stale clock (A-N1): set on insert and on every progress write, so a live burst is never abandoned.</summary>
    public DateTime LastActivityAtUtc { get; set; }

    [ConcurrencyCheck]
    public Guid Generation { get; set; }

    /// <summary>The executing process's per-process id; the key of its <see cref="JobHeartbeat"/> rows.</summary>
    public Guid? WorkerId { get; set; }

    /// <summary>Who asked for a manual run. A <c>SET NULL</c> key to <c>AspNetUsers</c>; never on a DTO.</summary>
    [StringLength(255)]
    public string? RequestedByUserId { get; set; }

    /// <summary>The headline count. Single writer: <see cref="Finish"/>, from the subtype's own counters (A7).</summary>
    public int? ItemsProcessed { get; set; }

    /// <inheritdoc cref="ItemsProcessed" />
    public int? ItemsFailed { get; set; }

    /// <summary><c>1</c> on an open <c>Held</c> row, else <c>NULL</c>; database-generated.</summary>
    public int? OpenHeldGuard { get; private set; }

    /// <summary>The job type while an exclusive run is <c>Running</c>, else <c>NULL</c>; database-generated.</summary>
    public int? ExclusiveGuard { get; private set; }

    /// <summary>Whether the run has neither finished nor been closed.</summary>
    [NotMapped]
    public bool IsOpen => FinishedAtUtc is null && Status is JobRunStatus.Running or JobRunStatus.Held;

    /// <summary>
    /// Finishes the run: status, category, finish time, duration, and the headline counts from the subtype's own
    /// counters. A held period keeps <see cref="Status"/> <c>Held</c> and gets no category.
    /// </summary>
    public void Finish(JobRunStatus status, JobFailureCategory? category, DateTime finishedAtUtc)
    {
        Status = status;
        FailureCategory = status is JobRunStatus.Succeeded or JobRunStatus.Skipped or JobRunStatus.Held ? null : category;
        if (status != JobRunStatus.Held)
        {
            HeldReason = null;
        }

        var finished = finishedAtUtc < StartedAtUtc ? StartedAtUtc : finishedAtUtc;
        FinishedAtUtc = finished;
        LastActivityAtUtc = finished;
        DurationMs = (long)(finished - StartedAtUtc).TotalMilliseconds;
        var (processed, failed) = HeadlineCounts();
        ItemsProcessed = processed;
        ItemsFailed = failed;
    }

    /// <summary>The headline counts (§4.2): processed and failed, from the subtype's counters.</summary>
    protected abstract (int Processed, int Failed) HeadlineCounts();
}

/// <summary>An exchange-rate sync run or purge (issue #349), moved onto the shared log; ids kept.</summary>
public class ExchangeRateSyncJobRun : JobRun
{
    public ExchangeRateSyncJobRun() : base(Dtos.Application.JobType.ExchangeRateSync)
    {
    }

    public ExchangeRateProvider Provider { get; set; } = ExchangeRateProvider.NorgesBank;

    /// <summary>The sync's own error kind, kept for its existing endpoints; <see cref="JobRun.FailureCategory"/> mirrors it.</summary>
    public ExchangeRateSyncErrorKind? ErrorKind { get; set; }

    public DateOnly? SpanFrom { get; set; }

    public DateOnly? SpanTo { get; set; }

    /// <summary>The run's currencies as a comma list.</summary>
    [StringLength(256)]
    public string CurrencyCodes { get; set; } = string.Empty;

    public int RowsInserted { get; set; }

    public int RowsSkipped { get; set; }

    public int RowsRejected { get; set; }

    public int RowsDeleted { get; set; }

    /// <summary>The sync's status vocabulary; ordinals 0–4 are shared with <see cref="JobRunStatus"/>.</summary>
    [NotMapped]
    public ExchangeRateSyncRunStatus SyncStatus => (ExchangeRateSyncRunStatus)Status;

    /// <summary>The sync's trigger vocabulary; ordinals are shared with <see cref="JobRunTrigger"/>.</summary>
    [NotMapped]
    public ExchangeRateSyncTrigger SyncTrigger => (ExchangeRateSyncTrigger)Trigger;

    protected override (int Processed, int Failed) HeadlineCounts() => (RowsInserted + RowsDeleted, RowsRejected);
}

/// <summary>A weather burst or manual run: the forecast fetches of the tracked points and the observation fetches.</summary>
public class WeatherJobRun : JobRun
{
    public WeatherJobRun() : base(Dtos.Application.JobType.Weather)
    {
    }

    public int PointsAttempted { get; set; }

    public int PointsSkippedNotExpired { get; set; }

    public int PointsFailed { get; set; }

    public int ForecastsStored { get; set; }

    public int ObservationDaysStored { get; set; }

    /// <summary>The most recent failure's kind, when a point failed.</summary>
    public WeatherFetchFailureKind? FailureKind { get; set; }

    protected override (int Processed, int Failed) HeadlineCounts() => (PointsAttempted, PointsFailed);
}

/// <summary>A file-storage maintenance pass, or a held period of one process.</summary>
public class FileStorageMaintenanceJobRun : JobRun
{
    public FileStorageMaintenanceJobRun() : base(Dtos.Application.JobType.FileStorageMaintenance)
    {
    }

    public int FilesErased { get; set; }

    public int AbandonedWritesSwept { get; set; }

    public int ObjectsScrubbed { get; set; }

    public int IntegrityFailures { get; set; }

    public int OrphansFound { get; set; }

    /// <summary>The failing startup check that holds the job — a check name, never a path.</summary>
    [StringLength(64)]
    public string? HeldByCheck { get; set; }

    protected override (int Processed, int Failed) HeadlineCounts() =>
        (FilesErased + AbandonedWritesSwept + ObjectsScrubbed, IntegrityFailures);
}

/// <summary>A thumbnail burst or manual run, or a held period of one process.</summary>
public class PhotoThumbnailJobRun : JobRun
{
    public PhotoThumbnailJobRun() : base(Dtos.Application.JobType.PhotoThumbnails)
    {
    }

    public int PhotosRendered { get; set; }

    public int PhotosFailed { get; set; }

    public int PhotosRequeued { get; set; }

    public int Crashes { get; set; }

    public int Hangs { get; set; }

    protected override (int Processed, int Failed) HeadlineCounts() => (PhotosRendered, PhotosFailed + Crashes + Hangs);
}

/// <summary>
/// A burst or manual run of the photo-original move (issue #428), or a held period of one process — storage
/// unavailable, or the back-off after a storage or database error.
/// </summary>
public class PhotoOriginalBackfillJobRun : JobRun
{
    public PhotoOriginalBackfillJobRun() : base(Dtos.Application.JobType.PhotoOriginalBackfill)
    {
    }

    /// <summary>Moved, with the database file removed because nothing else used it.</summary>
    public int PhotosMoved { get; set; }

    /// <summary>Moved, with the database file kept because something else still uses it.</summary>
    public int PhotosCopiedShared { get; set; }

    /// <summary>Parked for a reason of their own (a <c>PhotoOriginalBackfillFailures</c> code).</summary>
    public int PhotosParked { get; set; }

    /// <summary>Ticks cut short by a storage or database error; the move then backs off.</summary>
    public int Interruptions { get; set; }

    protected override (int Processed, int Failed) HeadlineCounts() => (PhotosMoved + PhotosCopiedShared, PhotosParked);
}

/// <summary>
/// A process's last tick of one job (§4.3), written at most once a minute, before any early return, so a process
/// that is idle, unconfigured or held is still seen to be alive. Keyed by the per-process id its run rows carry: a
/// restarted container draws a new one, so its predecessor's open rows lose their heartbeat and the sweep closes
/// <summary>
/// A burst of the file-analysis queue (issue #444): one row covers the ticks that claimed work, like the
/// thumbnail job's. Counts only.
/// </summary>
public class FileAnalysisJobRun : JobRun
{
    public FileAnalysisJobRun() : base(Dtos.Application.JobType.FileAnalysis)
    {
    }

    public int AnalysesCompleted { get; set; }

    public int AnalysesFailed { get; set; }

    public int AnalysesRefused { get; set; }

    protected override (int Processed, int Failed) HeadlineCounts() => (AnalysesCompleted, AnalysesFailed);
}

/// <summary>
/// A process's last tick of one job (§4.3), written at most once a minute, before any early return, so a process
/// that is idle, unconfigured or held is still seen to be alive. Keyed by the per-process id its run rows carry: a
/// restarted container draws a new one, so its predecessor's open rows lose their heartbeat and the sweep closes
/// them. No key to anything.
/// </summary>
[Table("JobHeartbeats")]
[PrimaryKey(nameof(JobType), nameof(WorkerId))]
public class JobHeartbeat
{
    public JobType JobType { get; set; }

    public Guid WorkerId { get; set; }

    public DateTime LastTickAtUtc { get; set; }
}
