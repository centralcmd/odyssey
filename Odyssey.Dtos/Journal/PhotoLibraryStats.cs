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
}

/// <summary>A count of active photos associated with a keyed entity (a tag id or a person contact id).</summary>
public sealed record PhotoCountByKey
{
    public Guid Key { get; set; }

    public int Count { get; set; }
}
