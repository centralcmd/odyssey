using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Odyssey.Dtos.Journal;

namespace Odyssey.Dtos.Application;

// ── Enums (issue #409 §4.1) ─────────────────────────────────────────────────────────────────────────
//
// Each is stored as a byte, so the ordinals are a persistence contract and never renumbered. Each also carries
// an Unknown member at 255: the sentinel TolerantEnumConverter reads a name it does not know into. It is never
// stored (the JobRuns CHECKs exclude it) and never sent by the server.

/// <summary>The background jobs that record runs. The discriminator of <c>JobRuns</c>.</summary>
[JsonConverter(typeof(TolerantEnumConverter<JobType>))]
public enum JobType : byte
{
    ExchangeRateSync = 1,
    Weather = 2,
    FileStorageMaintenance = 3,
    PhotoThumbnails = 4,

    /// <summary>The one-off move of legacy photo originals onto the photo store (issue #428).</summary>
    PhotoOriginalBackfill = 5,

    /// <summary>The AI file-analysis extraction queue (issue #444): work-driven, never run by hand.</summary>
    FileAnalysis = 6,
    /// <summary>The one-off move of every remaining <c>FileBlob</c> onto the file store (issue #448).</summary>
    FileBlobBackfill = 7,

    /// <summary>The instrument-price sync and its purges (issue #446).</summary>
    InstrumentPriceSync = 8,

    Unknown = byte.MaxValue,
}

/// <summary>What started a run. The ordinals are the exchange-rate sync's, so its rows copied across unchanged.</summary>
[JsonConverter(typeof(TolerantEnumConverter<JobRunTrigger>))]
public enum JobRunTrigger : byte
{
    Scheduled = 0,
    Manual = 1,

    /// <summary>An exchange-rate purge, which holds the sync's guard.</summary>
    Purge = 2,

    Unknown = byte.MaxValue,
}

/// <summary>A run's state. <c>0</c>–<c>4</c> are the exchange-rate sync's ordinals; <c>Held</c> is new.</summary>
[JsonConverter(typeof(TolerantEnumConverter<JobRunStatus>))]
public enum JobRunStatus : byte
{
    Running = 0,
    Succeeded = 1,
    PartiallySucceeded = 2,
    Failed = 3,
    Skipped = 4,

    /// <summary>A period during which one process could not run the job (§3.3 step 3). Open while unfinished.</summary>
    Held = 5,

    Unknown = byte.MaxValue,
}

/// <summary>
/// Why a run failed or only partly succeeded (§4.1.1). Ordinals 1–9 equal <c>ExchangeRateSyncErrorKind</c>, so the
/// migration copies the sync's error kind across unchanged; a guard test pins the equality.
/// </summary>
[JsonConverter(typeof(TolerantEnumConverter<JobFailureCategory>))]
public enum JobFailureCategory : byte
{
    ProviderUnavailable = 1,
    ProviderRateLimited = 2,
    ProviderMalformed = 3,
    UnsupportedInput = 4,
    Configuration = 5,
    Environment = 6,
    Cancelled = 7,
    Abandoned = 8,
    Internal = 9,
    Storage = 10,

    Unknown = byte.MaxValue,
}

/// <summary>Why a process could not run its job during a <c>Held</c> period (A6).</summary>
[JsonConverter(typeof(TolerantEnumConverter<JobHeldReason>))]
public enum JobHeldReason : byte
{
    /// <summary>This worker's file-storage checks fail.</summary>
    StorageCheckFailed = 1,

    /// <summary>The thumbnail breaker: three crashes or hangs within an hour.</summary>
    BreakerTripped = 2,

    /// <summary>A thumbnail decode hung; claiming waits for its thread.</summary>
    DecoderPaused = 3,

    /// <summary>The thumbnail decoder failed its security self-test.</summary>
    DecoderPolicyFailed = 4,

    /// <summary>The photo volume (thumbnails, originals) is unavailable or below its reserve in this process.</summary>
    StorageUnavailable = 5,

    /// <summary>The photo-original move waits out its back-off after a storage or database error.</summary>
    BackingOff = 6,

    Unknown = byte.MaxValue,
}

/// <summary>A job's health on the overview, by the precedence of §5.1 (first match wins).</summary>
[JsonConverter(typeof(TolerantEnumConverter<JobHealth>))]
public enum JobHealth : byte
{
    Healthy = 0,
    Running = 1,
    Failing = 2,
    Overdue = 3,
    Held = 4,
    NotTicking = 5,
    NeverRun = 6,
    NotConfigured = 7,

    Unknown = byte.MaxValue,
}

/// <summary>
/// Sortable keys of the run list (§5.2). <c>JobType</c> and <c>Status</c> sort by ordinal; <c>Duration</c> puts a
/// run without one (running, or an open held period) last in both directions.
/// </summary>
public enum JobRunSortBy
{
    StartedAt,
    Duration,
    JobType,
    Status,
}

// ── Overview (§5.1) ─────────────────────────────────────────────────────────────────────────────────

/// <summary><c>GET /api/background-jobs/overview</c>: every job's health and the log's totals.</summary>
public sealed record BackgroundJobsOverview
{
    public DateTime GeneratedAtUtc { get; set; }

    public List<BackgroundJobSummary> Jobs { get; set; } = [];

    public JobRunTotals Totals { get; set; } = new();

    public List<RunningJobRun> Running { get; set; } = [];

    public BackgroundJobsWorkerHealth WorkerHealth { get; set; } = new();
}

/// <summary>
/// One job on the overview. Every threshold the client compares against travels here, so the client restates
/// none (A10). Fields produced by the job's own domain are <c>null</c> without its detail-read claim — see
/// <see cref="CanReadDetail"/>.
/// </summary>
public sealed record BackgroundJobSummary
{
    public JobType JobType { get; set; }

    public JobHealth Health { get; set; }

    /// <summary>The last finished run that is not a held period.</summary>
    public JobRunReference? LastRun { get; set; }

    public DateTime? LastSuccessAtUtc { get; set; }

    public DateTime? LastHeartbeatAtUtc { get; set; }

    /// <summary>When the next scheduled run is due; <c>null</c> for a work-driven job.</summary>
    public DateTime? NextExpectedRunAtUtc { get; set; }

    /// <summary>Why a live process holds the job; <c>null</c> when none does, or without the detail-read claim.</summary>
    public JobHeldReason? HeldReason { get; set; }

    /// <summary>The failing file-storage check (a check name, never a path); <c>null</c> without <c>file-storage.read</c>.</summary>
    public string? HeldCheck { get; set; }

    /// <summary>When a held job resumes, when known (the thumbnail breaker, the photo-original back-off); <c>null</c> without the detail-read claim.</summary>
    public DateTime? HeldUntilUtc { get; set; }

    /// <summary>The caller holds every claim a manual run of this job needs. The claim list itself is never sent.</summary>
    public bool CanRunNow { get; set; }

    /// <summary>The caller holds this job's detail-read claim: a <c>null</c> count means "not shown", not "not recorded".</summary>
    public bool CanReadDetail { get; set; }

    /// <summary>The end of the manual-run cooldown; <c>null</c> when a manual run is available now.</summary>
    public DateTime? ManualRunAvailableAtUtc { get; set; }

    public int NotTickingAfterSeconds { get; set; }

    /// <summary>Twice the cadence of a scheduled job; <c>null</c> for a work-driven job.</summary>
    public int? OverdueAfterSeconds { get; set; }

    public JobRunTotals Totals { get; set; } = new();
}

/// <summary>A run named by the overview.</summary>
public sealed record JobRunReference
{
    public Guid Id { get; set; }

    public JobRunStatus Status { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }
}

/// <summary>The 24-hour and 7-day counts (§5.1 "Totals").</summary>
public sealed record JobRunTotals
{
    public JobRunTotalsWindow Last24h { get; set; } = new();

    public JobRunTotalsWindow Last7d { get; set; } = new();
}

/// <summary>
/// <see cref="Runs"/> counts finished runs that are not held periods; <see cref="Succeeded"/> counts
/// <c>Succeeded</c> and <c>PartiallySucceeded</c>; <see cref="Failed"/> counts <c>Failed</c>; <see cref="Held"/>
/// counts held periods, open or closed. A <c>Skipped</c> run is in <see cref="Runs"/> only.
/// </summary>
public sealed record JobRunTotalsWindow
{
    public int Runs { get; set; }

    public int Succeeded { get; set; }

    public int Failed { get; set; }

    public int Held { get; set; }
}

/// <summary>A run in progress, on the overview.</summary>
public sealed record RunningJobRun
{
    public Guid Id { get; set; }

    public JobType JobType { get; set; }

    public JobRunTrigger Trigger { get; set; }

    public DateTime StartedAtUtc { get; set; }
}

/// <summary>The worker-side signals of four jobs; each part is <c>null</c> without its job's detail-read claim.</summary>
public sealed record BackgroundJobsWorkerHealth
{
    public FileStorageWorkerHealth? FileStorage { get; set; }

    public ThumbnailWorkerHealth? Thumbnails { get; set; }

    /// <summary>How far the photo-original move has got (issue #428); counts and failure codes only.</summary>
    public PhotoBackfillStatus? PhotoOriginalBackfill { get; set; }

    /// <summary>How far the file move has got (issue #448); counts and failure codes only.</summary>
    public FileBackfillStatus? FileBlobBackfill { get; set; }
}

/// <summary>How many worker processes report failing file-storage checks, and which checks (names, never paths).</summary>
public sealed record FileStorageWorkerHealth
{
    public int FailingWorkers { get; set; }

    public List<string> FailingChecks { get; set; } = [];
}

/// <summary>The most severe fresh thumbnail worker state, and when the breaker resumes.</summary>
public sealed record ThumbnailWorkerHealth
{
    public ThumbnailWorkerState State { get; set; }

    public DateTime? ResumesAtUtc { get; set; }
}

// ── The run list and its detail (§5.2, §5.3) ─────────────────────────────────────────────────────────

/// <summary>
/// One row of <c>GET /api/background-jobs/runs</c>. <see cref="ItemsProcessed"/>, <see cref="ItemsFailed"/> and
/// <see cref="HeldReason"/> are <c>null</c> without the job's detail-read claim.
/// </summary>
public sealed record ExistingJobRun
{
    public Guid Id { get; set; }

    public JobType JobType { get; set; }

    public JobRunTrigger Trigger { get; set; }

    public JobRunStatus Status { get; set; }

    public JobFailureCategory? FailureCategory { get; set; }

    public JobHeldReason? HeldReason { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }

    public long? DurationMs { get; set; }

    public int? ItemsProcessed { get; set; }

    public int? ItemsFailed { get; set; }
}

/// <summary>
/// <c>GET /api/background-jobs/runs/{id}</c>: the common fields, and the job's own counters in a subtype
/// discriminated by <c>$kind</c> — not by <see cref="JobType"/>, which the base already carries (A8). A kind this
/// client does not know reads as this base type rather than failing. The job's own fields are <c>null</c> without
/// its detail-read claim; enum names only, never exception text.
/// </summary>
/// <remarks>
/// The discriminator is written and read by <see cref="JobRunDetailJsonConverter"/>, not by
/// <c>[JsonPolymorphic]</c>: System.Text.Json's <c>FallBackToBaseType</c> applies to serialising an unknown
/// <em>type</em> only, and an unknown discriminator on read still throws — the opposite of F8/FS1.
/// </remarks>
[JsonConverter(typeof(JobRunDetailJsonConverter))]
public record JobRunDetail
{
    public Guid Id { get; set; }

    public JobType JobType { get; set; }

    public JobRunTrigger Trigger { get; set; }

    public JobRunStatus Status { get; set; }

    public JobFailureCategory? FailureCategory { get; set; }

    public JobHeldReason? HeldReason { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }

    public DateTime? ClaimedAtUtc { get; set; }

    public DateTime LastActivityAtUtc { get; set; }

    public long? DurationMs { get; set; }

    public int? ItemsProcessed { get; set; }

    public int? ItemsFailed { get; set; }
}

/// <summary>
/// Writes a <see cref="JobRunDetail"/> with its <c>$kind</c> first, and reads one by its <c>$kind</c> — a kind it
/// does not know, or none, as the base type (issue #409 §5, F8/FS1). The allow-list of kinds is
/// <see cref="Kinds"/>; nothing else is ever instantiated from a payload.
/// </summary>
public sealed class JobRunDetailJsonConverter : JsonConverter<JobRunDetail>
{
    public const string DiscriminatorName = "$kind";

    /// <summary>The explicit allow-list: each kind is a <see cref="Application.JobType"/> name.</summary>
    public static readonly IReadOnlyDictionary<string, Type> Kinds = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [nameof(Application.JobType.ExchangeRateSync)] = typeof(ExchangeRateSyncJobRunDetail),
        [nameof(Application.JobType.Weather)] = typeof(WeatherJobRunDetail),
        [nameof(Application.JobType.FileStorageMaintenance)] = typeof(FileStorageMaintenanceJobRunDetail),
        [nameof(Application.JobType.PhotoThumbnails)] = typeof(PhotoThumbnailJobRunDetail),
        [nameof(Application.JobType.PhotoOriginalBackfill)] = typeof(PhotoOriginalBackfillJobRunDetail),
        [nameof(Application.JobType.FileAnalysis)] = typeof(FileAnalysisJobRunDetail),
        [nameof(Application.JobType.FileBlobBackfill)] = typeof(FileBlobBackfillJobRunDetail),
        [nameof(Application.JobType.InstrumentPriceSync)] = typeof(InstrumentPriceSyncJobRunDetail),
    };

    public override JobRunDetail? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A run detail must be a JSON object.");
        }

        var kind = root.TryGetProperty(DiscriminatorName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        if (kind is not null && Kinds.TryGetValue(kind, out var type))
        {
            return (JobRunDetail?)root.Deserialize(type, options);
        }

        var shape = root.Deserialize<BaseShape>(options)!;
        return new JobRunDetail
        {
            Id = shape.Id,
            JobType = shape.JobType,
            Trigger = shape.Trigger,
            Status = shape.Status,
            FailureCategory = shape.FailureCategory,
            HeldReason = shape.HeldReason,
            StartedAtUtc = shape.StartedAtUtc,
            FinishedAtUtc = shape.FinishedAtUtc,
            ClaimedAtUtc = shape.ClaimedAtUtc,
            LastActivityAtUtc = shape.LastActivityAtUtc,
            DurationMs = shape.DurationMs,
            ItemsProcessed = shape.ItemsProcessed,
            ItemsFailed = shape.ItemsFailed,
        };
    }

    public override void Write(Utf8JsonWriter writer, JobRunDetail value, JsonSerializerOptions options)
    {
        var runtime = value.GetType();
        var kind = Kinds.FirstOrDefault(pair => pair.Value == runtime).Key;
        var body = runtime == typeof(JobRunDetail)
            ? JsonSerializer.SerializeToElement(BaseShape.From(value), options)
            : JsonSerializer.SerializeToElement(value, runtime, options);

        writer.WriteStartObject();
        if (kind is not null)
        {
            writer.WriteString(DiscriminatorName, kind);
        }

        foreach (var property in body.EnumerateObject())
        {
            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    /// <summary>The base's fields as a plain type, so reading or writing the base never re-enters this converter.</summary>
    private sealed class BaseShape
    {
        public Guid Id { get; set; }

        public JobType JobType { get; set; }

        public JobRunTrigger Trigger { get; set; }

        public JobRunStatus Status { get; set; }

        public JobFailureCategory? FailureCategory { get; set; }

        public JobHeldReason? HeldReason { get; set; }

        public DateTime StartedAtUtc { get; set; }

        public DateTime? FinishedAtUtc { get; set; }

        public DateTime? ClaimedAtUtc { get; set; }

        public DateTime LastActivityAtUtc { get; set; }

        public long? DurationMs { get; set; }

        public int? ItemsProcessed { get; set; }

        public int? ItemsFailed { get; set; }

        public static BaseShape From(JobRunDetail value) => new()
        {
            Id = value.Id,
            JobType = value.JobType,
            Trigger = value.Trigger,
            Status = value.Status,
            FailureCategory = value.FailureCategory,
            HeldReason = value.HeldReason,
            StartedAtUtc = value.StartedAtUtc,
            FinishedAtUtc = value.FinishedAtUtc,
            ClaimedAtUtc = value.ClaimedAtUtc,
            LastActivityAtUtc = value.LastActivityAtUtc,
            DurationMs = value.DurationMs,
            ItemsProcessed = value.ItemsProcessed,
            ItemsFailed = value.ItemsFailed,
        };
    }
}

public sealed record ExchangeRateSyncJobRunDetail : JobRunDetail
{
    public Finance.ExchangeRateSyncErrorKind? ErrorKind { get; set; }

    public DateOnly? SpanFrom { get; set; }

    public DateOnly? SpanTo { get; set; }

    public List<string>? CurrencyCodes { get; set; }

    public int? RowsInserted { get; set; }

    public int? RowsSkipped { get; set; }

    public int? RowsRejected { get; set; }

    public int? RowsDeleted { get; set; }
}

public sealed record WeatherJobRunDetail : JobRunDetail
{
    public int? PointsAttempted { get; set; }

    public int? PointsSkippedNotExpired { get; set; }

    public int? PointsFailed { get; set; }

    public int? ForecastsStored { get; set; }

    public int? ObservationDaysStored { get; set; }

    public WeatherFetchFailureKind? FailureKind { get; set; }
}

public sealed record FileStorageMaintenanceJobRunDetail : JobRunDetail
{
    public int? FilesErased { get; set; }

    public int? AbandonedWritesSwept { get; set; }

    public int? ObjectsScrubbed { get; set; }

    public int? IntegrityFailures { get; set; }

    public int? OrphansFound { get; set; }

    /// <summary>The failing check that held the job (a check name, never a path).</summary>
    public string? HeldByCheck { get; set; }
}

public sealed record PhotoThumbnailJobRunDetail : JobRunDetail
{
    public int? PhotosRendered { get; set; }

    public int? PhotosFailed { get; set; }

    public int? PhotosRequeued { get; set; }

    public int? Crashes { get; set; }

    public int? Hangs { get; set; }
}

public sealed record PhotoOriginalBackfillJobRunDetail : JobRunDetail
{
    /// <summary>Moved, with their database file removed because nothing else used it.</summary>
    public int? PhotosMoved { get; set; }

    /// <summary>Moved, with their database file kept because an attachment still uses it.</summary>
    public int? PhotosCopiedShared { get; set; }

    /// <summary>Parked for a reason of their own; not retried until an operator clears them.</summary>
    public int? PhotosParked { get; set; }

    /// <summary>Ticks cut short by a storage or database error, after which the move backs off.</summary>
    public int? Interruptions { get; set; }
}

/// <summary>
/// A file-analysis burst (issue #444): how many queued analyses it finished, failed, or refused before
/// sending. Counts only — never a file, account or user.
/// </summary>
public sealed record FileAnalysisJobRunDetail : JobRunDetail
{
    /// <summary>Analyses that extracted their candidates.</summary>
    public int? AnalysesCompleted { get; set; }

    /// <summary>Analyses that failed after the document may have been sent (provider error, interruption).</summary>
    public int? AnalysesFailed { get; set; }

    /// <summary>Analyses refused before anything was sent (settings changed, analysis switched off, requester gone).</summary>
    public int? AnalysesRefused { get; set; }
}

/// <summary>
/// A file-move burst (issue #448): how many files it moved into each area, how many it parked, and how many ticks an
/// outage cut short. Counts only — never a file, name or path.
/// </summary>
public sealed record FileBlobBackfillJobRunDetail : JobRunDetail
{
    /// <summary>Moved to the finance area.</summary>
    public int? FinanceFilesMoved { get; set; }

    /// <summary>Moved to the journal area.</summary>
    public int? JournalFilesMoved { get; set; }

    /// <summary>Moved to the contact-avatar area.</summary>
    public int? ContactAvatarsMoved { get; set; }

    /// <summary>Parked for a reason of their own; not retried until an operator clears them.</summary>
    public int? FilesParked { get; set; }

    /// <summary>Ticks cut short by a storage or database error, after which the move backs off.</summary>
    public int? Interruptions { get; set; }
}

/// <summary><c>202</c> of <c>POST /api/background-jobs/{jobType}/runs</c>.</summary>
public sealed record JobRunStarted
{
    public Guid Id { get; set; }
}

/// <summary>The stable <c>code</c> extensions of the manual-run <c>409</c> (F5, FS2); the client never renders <c>detail</c>.</summary>
public static class JobRunConflictCodes
{
    public const string AlreadyRunning = "job-run.already-running";

    public const string Held = "job-run.held";

    /// <summary>The job has no manual run (issue #444): it only ever runs work a user consented to.</summary>
    public const string ManualRunUnsupported = "job-run.manual-run-unsupported";
}

/// <summary>
/// The run list's query (§5.2). Tighter than the shared caps: at most 100 rows a page and an offset of at most
/// 100 000, so a deep page never scans the whole log; <c>search</c> is not supported. The dates are calendar days
/// in <see cref="Tz"/>, converted on the server to <c>[local midnight, next local midnight)</c>.
/// </summary>
public sealed class JobRunsQueryParams : QueryParams<JobRunSortBy>, IValidatableObject
{
    public const int MaxLimit = 100;

    public const int MaxOffset = 100_000;

    public const int DefaultLimit = 25;

    public const int TimeZoneMaxLength = 64;

    /// <summary>A shape allow-list run before any time-zone lookup (S-N3).</summary>
    public const string TimeZonePattern = @"^[A-Za-z]+(?:[/_+\-][A-Za-z0-9]+){0,4}$";

    /// <summary>One generic message for every time-zone rejection: it echoes nothing the caller sent.</summary>
    public const string TimeZoneMessage = "The time zone is not recognised.";

    /// <summary>
    /// The earliest date the run-log filter accepts. Nothing is recorded before it, and it keeps the local-midnight
    /// conversion clear of <see cref="DateTime.MinValue"/>, where a zone offset underflows.
    /// </summary>
    public static readonly DateOnly MinStartedDate = new(1980, 1, 1);

    /// <summary>
    /// The latest date the run-log filter accepts. The <c>to</c> bound is the NEXT day's midnight, so the last day
    /// of the calendar (9999-12-31) would overflow; a year short of it leaves room for every zone offset.
    /// </summary>
    public static readonly DateOnly MaxStartedDate = new(9998, 12, 31);

    /// <summary>One message for an out-of-range date; it names the bounds, never the value sent.</summary>
    public const string DateRangeMessage = "The date must be between 1980-01-01 and 9998-12-31.";

    public JobRunsQueryParams()
    {
        Limit = DefaultLimit;
    }

    [EnumFilter(typeof(JobType), 6)]
    public JobType[]? JobTypes { get; set; }

    [EnumFilter(typeof(JobRunStatus), 6)]
    public JobRunStatus[]? Statuses { get; set; }

    [EnumFilter(typeof(JobRunTrigger), 3)]
    public JobRunTrigger[]? Triggers { get; set; }

    public DateOnly? StartedFrom { get; set; }

    public DateOnly? StartedTo { get; set; }

    [StringLength(TimeZoneMaxLength, ErrorMessage = TimeZoneMessage)]
    [RegularExpression(TimeZonePattern, ErrorMessage = TimeZoneMessage)]
    public string? Tz { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrEmpty(Search))
        {
            yield return new ValidationResult("Search is not supported on the run list.", [nameof(Search)]);
        }

        if (Limit is < 1 or > MaxLimit)
        {
            yield return new ValidationResult($"Limit must be between 1 and {MaxLimit}.", [nameof(Limit)]);
        }

        if (Offset > MaxOffset)
        {
            yield return new ValidationResult($"Offset must be at most {MaxOffset}.", [nameof(Offset)]);
        }

        if (StartedFrom is { } start && (start < MinStartedDate || start > MaxStartedDate))
        {
            yield return new ValidationResult(DateRangeMessage, [nameof(StartedFrom)]);
        }

        if (StartedTo is { } end && (end < MinStartedDate || end > MaxStartedDate))
        {
            yield return new ValidationResult(DateRangeMessage, [nameof(StartedTo)]);
        }

        if (StartedFrom is { } from && StartedTo is { } to && from > to)
        {
            yield return new ValidationResult("The start of the date range is after its end.", [nameof(StartedFrom)]);
        }

        if ((StartedFrom is not null || StartedTo is not null) && string.IsNullOrEmpty(Tz))
        {
            yield return new ValidationResult(TimeZoneMessage, [nameof(Tz)]);
        }
    }
}

/// <summary>An instrument-price sync run or purge (issue #446 §4.6). Counts only — never a symbol or a source symbol.</summary>
public sealed record InstrumentPriceSyncJobRunDetail : JobRunDetail
{
    public int? InstrumentsAttempted { get; set; }

    public int? InstrumentsSucceeded { get; set; }

    public int? InstrumentsFailed { get; set; }

    /// <summary>Instruments a request limit left for the next hour (issue #447 A8) — not a failure.</summary>
    public int? InstrumentsDeferred { get; set; }

    public int? PricesInserted { get; set; }

    public int? FallbacksUsed { get; set; }

    public int? RequestsMade { get; set; }

    public int? PricesDeleted { get; set; }
}
