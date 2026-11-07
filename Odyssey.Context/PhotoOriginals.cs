using System.ComponentModel.DataAnnotations;

namespace Odyssey.Context;

/// <summary>
/// The per-year bucket counter of the <c>PhotoOriginals</c> layout (issue #428 §3.5): originals live under
/// <c>photo-originals/&lt;year&gt;/&lt;bucket&gt;/</c>, at most 10 000 per bucket. A slot is taken under
/// <c>SELECT … FOR UPDATE</c> in its own short transaction before the file is written, so there is no
/// concurrency token: the row lock serializes the writers.
/// </summary>
public class PhotoOriginalBucket
{
    /// <summary>The UTC year of the photo's <c>CreatedAt</c>.</summary>
    [Key]
    public int Year { get; set; }

    public int CurrentBucket { get; set; }

    public int PhotosWritten { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// The single row electing one runner of the photo-original backfill per tick (issue #428 §3.4), plus the
/// environmental back-off, which must survive a restart and be shared by replicas.
/// </summary>
public class PhotoOriginalBackfillLease
{
    public const byte SingletonId = 1;

    [Key]
    public byte Id { get; set; }

    public Guid? HolderId { get; set; }

    public DateTime? LeaseUntilUtc { get; set; }

    [ConcurrencyCheck]
    public Guid Version { get; set; }

    public int ConsecutiveEnvironmentalFailures { get; set; }

    public DateTime? NextAttemptAtUtc { get; set; }

    public DateTime? LastRunAtUtc { get; set; }

    /// <summary>How many backfilled photos kept their file because something else still used it.</summary>
    public int CopiedSharedCount { get; set; }
}

/// <summary>
/// A legacy photo the backfill could not move for a deterministic reason (issue #428 §3.4 step 8). It is not
/// retried until an operator deletes this row. Holds no file name, path or hash.
/// </summary>
public class PhotoOriginalBackfillFailure
{
    [Key]
    public Guid PhotoId { get; set; }

    [Required]
    [MaxLength(16)]
    public string Code { get; set; } = string.Empty;

    public DateTime RecordedAtUtc { get; set; }
}

/// <summary>The deterministic backfill failure codes. Only these reach a row or a log line.</summary>
public static class PhotoOriginalBackfillFailureCodes
{
    public const string Hash = "hash";
    public const string Read = "read";
    public const string TooLarge = "too-large";
}
