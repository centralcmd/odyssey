namespace Odyssey.Dtos.Journal;

/// <summary>
/// Aggregate library counters for the Overview panel (issue #321), computed server-side over the active
/// (non-archived) library so the panel needn't pull every photo. Per-tag and per-person counts come back
/// keyed by id; the client joins them to the tag/person names it already holds.
/// </summary>
public sealed record PhotoLibraryStats
{
    /// <summary>Active (non-archived) photos in the library.</summary>
    public int TotalCount { get; set; }

    /// <summary>Active photos marked as favourites.</summary>
    public int FavouriteCount { get; set; }

    /// <summary>Active-photo count per tag id (tags with no active photos are omitted).</summary>
    public IReadOnlyList<PhotoCountByKey> TagCounts { get; set; } = [];

    /// <summary>Active-photo count per Person contact id (people with no active photos are omitted).</summary>
    public IReadOnlyList<PhotoCountByKey> PersonCounts { get; set; } = [];

    /// <summary>Active photos whose thumbnail job is waiting or running (issue #362).</summary>
    public int ThumbnailsPending { get; set; }

    /// <summary>Active photos whose thumbnail job failed after its retries.</summary>
    public int ThumbnailsFailed { get; set; }

    /// <summary>Active photos the decoder will not render (type, size, a crash or a hang).</summary>
    public int ThumbnailsUnsupported { get; set; }

    /// <summary>The thumbnail worker's state; <c>null</c> unless the caller holds <c>system-settings.read</c>.</summary>
    public ThumbnailWorkerState? ThumbnailWorkerState { get; set; }

    /// <summary>When a <c>Tripped</c> worker resumes claiming, UTC; <c>null</c> otherwise, and under the same claim rule.</summary>
    public DateTime? ThumbnailWorkerResumesAtUtc { get; set; }
}

/// <summary>A count of active photos associated with a keyed entity (a tag id or a person contact id).</summary>
public sealed record PhotoCountByKey
{
    public Guid Key { get; set; }

    public int Count { get; set; }
}
