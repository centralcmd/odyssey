namespace Odyssey.Dtos.Journal;

// Per-resource allowlisted sort keys for the Photos module list endpoints (issue #321, following the
// Journal/Finance ListSortKeys pattern). Each member is a sortable column the resource's list surface
// exposes; the query params bind SortBy as one of these (an unbindable value is rejected, not coerced).

/// <summary>Sortable keys for the photos list.</summary>
public enum PhotoSortBy
{
    TakenAt,
    Title,
    CreatedAt,
}

/// <summary>Sortable keys for the photo-tags list.</summary>
public enum PhotoTagSortBy
{
    Name,
    Status,
}

/// <summary>Sortable keys for the albums list.</summary>
public enum PhotoAlbumSortBy
{
    Name,
    CreatedAt,
    Status,
}
