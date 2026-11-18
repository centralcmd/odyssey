using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// The per-feature, per-year bucket counter of the file layouts on the object store (issue #448 §3.6): files live under
/// <c>&lt;root&gt;/&lt;year&gt;/&lt;bucket&gt;/</c>, at most 10 000 per bucket. A slot is taken under <c>SELECT … FOR UPDATE</c>
/// in its own short transaction before the file is written, so there is no concurrency token: the row lock serializes
/// the writers. Photos keep their own <see cref="PhotoOriginalBucket"/>.
/// </summary>
[PrimaryKey(nameof(FeatureKey), nameof(Year))]
public class StoredFileBucket
{
    /// <summary>A storage-feature catalogue key.</summary>
    [MaxLength(64)]
    public string FeatureKey { get; set; } = string.Empty;

    /// <summary>The UTC year of the file's <c>UploadedAtUtc</c>.</summary>
    public int Year { get; set; }

    public int CurrentBucket { get; set; }

    public int ObjectsWritten { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// The single row electing one runner of the file move per tick (issue #448 §3.5), plus the environmental back-off,
/// which must survive a restart and be shared by replicas. The shape of <see cref="PhotoOriginalBackfillLease"/>.
/// </summary>
public class FileBlobBackfillLease
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
}

/// <summary>
/// A legacy file the move could not handle for a deterministic reason (issue #448 §3.5 step 8). It is not retried until
/// an operator deletes this row. Holds no file name, path or hash.
/// <para>
/// <b>No foreign key to <see cref="FileMetadata"/></b>, deliberately (issue #448 §4.4): <c>FileReferenceCounter</c>
/// discovers every key to <c>FileMetadata</c> as an attachment reference, so a key here would make a parked file count as
/// "used elsewhere", add an <c>EXISTS</c> to every <c>/api/files</c> query and trip the area classifier's guard. The job
/// removes rows whose file is gone, and every count joins to <c>FileMetadata</c>, so an orphan is never counted.
/// </para>
/// </summary>
public class FileBlobBackfillFailure
{
    [Key]
    public Guid FileId { get; set; }

    [Required]
    [MaxLength(16)]
    public string Code { get; set; } = string.Empty;

    public DateTime RecordedAtUtc { get; set; }
}

/// <summary>The file move's failure codes. Only these reach a row or a log line.</summary>
public static class FileBlobBackfillFailureCodes
{
    public const string Hash = "hash";
    public const string Read = "read";
    public const string TooLarge = "too-large";

    /// <summary>A stored content type with no extension mapping, from an older allow-list.</summary>
    public const string Type = "type";

    /// <summary>Neither a check this job makes nor an outage: a store contract or path-safety refusal, say.</summary>
    public const string Error = "error";

    public static readonly IReadOnlyList<string> All = [Hash, Read, TooLarge, Type, Error];
}
