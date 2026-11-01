using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// One deploy-time file volume (issue #346 §4.1). Its identity is its configured name plus the GUID in the
/// volume's <c>.odyssey-volume.json</c> marker, which equals <see cref="Id"/>. There is deliberately no path
/// column: a path is deploy-time configuration (<c>FileStorage:Volumes:&lt;name&gt;:RootPath</c>), and an
/// editable one would be an arbitrary filesystem read/write primitive.
/// </summary>
/// <remarks>
/// Rows are never seeded. The API's startup verifier inserts one on first sight of a configured name, or
/// adopts one from a marker after a database reset.
/// </remarks>
[Index(nameof(Name), IsUnique = true)]
public class FileStorageVolume
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>The configuration name, lowercase.</summary>
    [Required]
    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    /// <summary>When the marker was written, or adopted.</summary>
    public DateTime? InitializedAtUtc { get; set; }

    /// <summary>The last successful startup verification.</summary>
    public DateTime LastSeenAtUtc { get; set; }

    /// <summary>The disk-reconciliation pass in progress.</summary>
    public int ReconciliationPass { get; set; }

    /// <summary>
    /// The relative path of the last visited folder of the pass in progress; the next tick resumes after it in
    /// segment-wise ordinal order. Holds folder names only — never a file name.
    /// </summary>
    [MaxLength(512)]
    public string? ReconciliationCursor { get; set; }

    /// <summary>Orphans found by the last completed pass.</summary>
    public int? LastPassOrphanCount { get; set; }

    /// <summary>Set by the reconciliation breaker; cleared by a completed pass below the threshold.</summary>
    public DateTime? ReconciliationHaltedAtUtc { get; set; }

    /// <summary>Files under <c>.quarantine</c>, recomputed by the quarantine purge.</summary>
    public int QuarantinedFileCount { get; set; }
}

/// <summary>The lifecycle of a <see cref="StoredObject"/>. Stored as a byte; the ordinals are persisted.</summary>
public enum StoredObjectState : byte
{
    /// <summary>Reserved by a write that has not been committed by its caller. Never readable.</summary>
    Pending = 0,

    /// <summary>The file is on disk and the caller's referencing row committed with it.</summary>
    Committed = 1,

    /// <summary>Released by its consumer; unreadable at once, removed from disk by the maintenance job.</summary>
    PendingDelete = 2,
}

/// <summary>
/// One file in a filesystem volume (issue #346 §4.2). The database is the source of truth: a
/// <see cref="StoredObjectState.Committed"/> row always has its file, and every other disagreement between
/// the two is an orphan the maintenance job finds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Personal data.</b> <see cref="RelativePath"/> carries the sanitized original file name of the user's
/// upload (unless the feature declares opaque naming), so it is never logged, never returned by an endpoint
/// and is out of scope for the data export.
/// </para>
/// <para>
/// <b>Consumer rule.</b> A table referencing this one declares a <c>RESTRICT</c> key, belongs to exactly one
/// storage feature, and releases through <c>IObjectStore.StageRelease</c> in the same save that removes or
/// nulls its reference — never by removing this row or deleting a file.
/// </para>
/// </remarks>
public class StoredObject
{
    [Key]
    public Guid Id { get; set; }

    public Guid VolumeId { get; set; }

    /// <summary>The catalogue's canonical PascalCase feature key.</summary>
    [Required]
    [MaxLength(64)]
    public string FeatureKey { get; set; } = string.Empty;

    /// <summary>The file's path relative to the volume root, composed by the store and re-validated on use.</summary>
    [Required]
    [MaxLength(512)]
    public string RelativePath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>Lowercase hex SHA-256 of the content; null only while <see cref="StoredObjectState.Pending"/>.</summary>
    [MaxLength(64)]
    public string? Sha256Hash { get; set; }

    /// <summary>
    /// The concurrency token: a caller's commit is <c>UPDATE … WHERE State = Pending</c>, so a reservation the
    /// maintenance job already swept fails the caller's save instead of committing a reference to a deleted file.
    /// </summary>
    [ConcurrencyCheck]
    public StoredObjectState State { get; set; }

    /// <summary>Set and cleared only by the integrity scrub. Deliberately NOT a concurrency token.</summary>
    public DateTime? IntegrityFaultAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? DeleteRequestedAtUtc { get; set; }

    /// <summary>Removed on the next maintenance pass, regardless of the deletion grace.</summary>
    public bool DeleteImmediately { get; set; }

    public DateTime? LastVerifiedAtUtc { get; set; }
}

/// <summary>
/// A file on a volume with no <see cref="StoredObject"/> row, seen by the disk reconciliation (issue #346
/// §4.4). It is quarantined only after two passes and 24 hours, and only while the volume's breaker is not
/// tripped. Holds a file name, so it is personal data like <see cref="StoredObject.RelativePath"/>.
/// </summary>
public class FileStorageOrphanSighting
{
    /// <summary>Also the quarantine sub-folder name, which makes a quarantine target unique by construction.</summary>
    [Key]
    public Guid Id { get; set; }

    public Guid VolumeId { get; set; }

    [Required]
    [MaxLength(512)]
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Never moved forward while the sighting exists.</summary>
    public DateTime FirstSeenAtUtc { get; set; }

    public int LastSeenPass { get; set; }
}

/// <summary>
/// The single row that elects one runner of the file-store maintenance job (issue #346 Amendment A3). A run
/// claims it with a tracked save on <see cref="Version"/>, renews it while it works and clears it when done;
/// a crashed holder's lease simply expires. The same claim pattern as <c>WeatherLocationSchedule</c>.
/// </summary>
public class FileStorageMaintenanceLease
{
    /// <summary>The one row's key, always <see cref="SingletonId"/> (a CHECK enforces it).</summary>
    public const byte SingletonId = 1;

    [Key]
    public byte Id { get; set; }

    public Guid? HolderId { get; set; }

    public DateTime? LeaseUntilUtc { get; set; }

    [ConcurrencyCheck]
    public Guid Version { get; set; }

    /// <summary>When a full maintenance pass last finished; the status endpoint's staleness signal.</summary>
    public DateTime? LastCompletedAtUtc { get; set; }
}

/// <summary>
/// A worker process whose file-storage startup check is failing, so its maintenance job is held (issue #346 A3.2 as
/// amended). One row per process, keyed by an id the process draws at start: replicas never overwrite each other's
/// verdict. A failing process refreshes its row on every re-check; a process whose check passes deletes its own row,
/// and rows nobody has refreshed within the reporting window are ignored and swept, so a stopped worker's verdict
/// does not linger.
/// </summary>
[Index(nameof(LastReportedAtUtc))]
public class FileStorageWorkerVerdict
{
    [Key]
    public Guid WorkerId { get; set; }

    /// <summary>The failing check's name, such as <c>VolumeLost</c>. Never a path or a message.</summary>
    [Required]
    [MaxLength(64)]
    public string FailedCheck { get; set; } = string.Empty;

    /// <summary>When this process first failed with <see cref="FailedCheck"/>.</summary>
    public DateTime FailedAtUtc { get; set; }

    /// <summary>When this process last re-ran the checks and they still failed.</summary>
    public DateTime LastReportedAtUtc { get; set; }
}
