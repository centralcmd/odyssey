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
