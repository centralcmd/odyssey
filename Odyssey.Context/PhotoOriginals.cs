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
