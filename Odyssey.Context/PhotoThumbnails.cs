using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Odyssey.Dtos.Journal;

namespace Odyssey.Context;

// Photo thumbnails (issue #362 Draft v7, Amendment v7.1). The first feature of the filesystem object store
// (#346): the WebP files live on a volume, these rows say which. All five tables are derived data — nothing here
// is exported, and every row can be regenerated from the photo's own file.

/// <summary>One rendition of one photo, pointing at its stored object.</summary>
public class PhotoThumbnail
{
    [Key]
    public Guid PhotoThumbnailId { get; set; }

    public Guid PhotoId { get; set; }

    public ThumbnailSize Size { get; set; }

    /// <summary>RESTRICT to <c>StoredObjects</c> (#346 consumer rule); released through <c>StageRelease</c>.</summary>
    public Guid StoredObjectId { get; set; }

    public int PixelWidth { get; set; }

    public int PixelHeight { get; set; }

    public int RecipeVersion { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

public enum PhotoThumbnailJobState : byte
{
    Pending = 0,
    Running = 1,
    Done = 2,
    Failed = 3,
    Unsupported = 4,
}

public enum PhotoThumbnailJobPriority : byte
{
    /// <summary>A new upload, within its uploader's share.</summary>
    Interactive = 0,

    /// <summary>The backfill, recipe resets and every rename re-run.</summary>
    Background = 1,
}

/// <summary>
/// The durable queue: one row per photo (§3.3, §4.2). <see cref="Generation"/> is the only concurrency token and
/// every write bumps it, renames included; a worker acts only on a <c>Running</c> row carrying its own
/// <see cref="RunId"/>.
/// </summary>
[Table("PhotoThumbnailJobs")]
public class PhotoThumbnailJobEntry
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid PhotoId { get; set; }

    public PhotoThumbnailJobState State { get; set; }

    [ConcurrencyCheck]
    public int Generation { get; set; }

    /// <summary>Counted at claim; an outage, a shutdown or a superseded run gives it back.</summary>
    public int Attempts { get; set; }

    /// <summary>Expired leases: a second one is <c>Unsupported (crash)</c>. Never reset.</summary>
    public int LeaseExpiries { get; set; }

    public Guid? RunId { get; set; }

    /// <summary>The run a reclaim took over, so its live worker can prove it was alive and give the expiry back.</summary>
    public Guid? ReclaimedRunId { get; set; }

    /// <summary>The <c>Crash</c> incident that reclaim wrote. No key: the incident outlives the photo.</summary>
    public long? ReclaimIncidentId { get; set; }

    /// <summary>A rename landed on a running job; its success becomes a re-run.</summary>
    public bool RerunRequested { get; set; }

    public PhotoThumbnailJobPriority Priority { get; set; }

    /// <summary>The cooldown anchor: a rename never brings a re-run closer than 10 minutes after the last claim.</summary>
    public DateTime? LastClaimedAtUtc { get; set; }

    /// <summary>0 for a new job; set at claim, so a recipe bump finds every job attempted under an older one.</summary>
    public int AttemptedRecipeVersion { get; set; }

    public DateTime NextAttemptAtUtc { get; set; }

    public DateTime? LeaseUntilUtc { get; set; }

    /// <summary>A §9 code only (<c>type</c>, <c>decode</c>, …) — never an exception message or a name.</summary>
    [MaxLength(32)]
    public string? FailureCode { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// The single bucket counter (§3.2.1): photos written to the current bucket. Incremented in its own save before
/// the files are written, so a crash can only under-fill a bucket.
/// </summary>
public class PhotoThumbnailBucket
{
    public const byte SingletonId = 1;

    [Key]
    public byte Id { get; set; }

    public int CurrentBucket { get; set; }

    [ConcurrencyCheck]
    public int PhotosWritten { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

public enum PhotoThumbnailIncidentKind : byte
{
    Crash = 1,
    Timeout = 2,

    /// <summary>A bounded-pause restart; feeds only the one-restart-per-hour rule.</summary>
    Restart = 3,
}

/// <summary>
/// A decoder crash, hang or restart, for the global breaker. <b>No photo id and no key</b>: deleting a photo must
/// not reset the breaker, and the row holds no personal data.
/// </summary>
public class PhotoThumbnailIncident
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public PhotoThumbnailIncidentKind Kind { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    /// <summary>The worker host's name, on <c>Restart</c> rows only, so the restart limit binds per replica.</summary>
    [MaxLength(64)]
    public string? WorkerName { get; set; }
}

/// <summary>
/// One worker's thumbnail state, so the API can report it (§3.6, Amendment v7.1 §2). Keyed by host name, which a
/// restarted container keeps.
/// </summary>
public class PhotoThumbnailWorkerHeartbeat
{
    [Key]
    [MaxLength(64)]
    public string WorkerName { get; set; } = string.Empty;

    /// <summary><c>Running</c> to <c>StorageUnavailable</c>; <c>NotRunning</c> is never stored — it is the absence of a fresh row.</summary>
    public ThumbnailWorkerState State { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
