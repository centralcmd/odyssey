using System.ComponentModel.DataAnnotations;
using Odyssey.Dtos;

namespace Odyssey.Dtos.Journal;

// Per-endpoint list-query models for the Photos module (issue #321). Each closes the generic
// QueryParams base over its own sort-key enum (search + SortBy + sort direction + offset/limit) and adds
// only the filters that endpoint exposes. Array filters bind case-insensitively from the matching
// camelCase query key (tagIds → TagIds) and are length-capped so an over-cap array is rejected 400 by
// [ApiController] model validation (§9/§14). These are sealed class rather than record only because they
// are query-string binding models.

/// <summary>Photos list query: free-text (title/caption/location) search + tag/person/album/taken-date/archival filters.</summary>
public sealed class PhotosQueryParams : QueryParams<PhotoSortBy>
{
    [MaxLength(PhotoLimits.MaxLinksPerKind)]
    public Guid[]? TagIds { get; set; }

    [MaxLength(PhotoLimits.MaxLinksPerKind)]
    public Guid[]? PersonIds { get; set; }

    [MaxLength(PhotoLimits.MaxLinksPerKind)]
    public Guid[]? AlbumIds { get; set; }

    /// <summary>Inclusive lower bound on <c>TakenAt</c>.</summary>
    public DateTime? From { get; set; }

    /// <summary>Inclusive upper bound on <c>TakenAt</c>.</summary>
    public DateTime? To { get; set; }

    /// <summary>When true, restrict to favourited photos.</summary>
    public bool? FavouritesOnly { get; set; }

    public ArchivalStatus? Status { get; set; }
}

/// <summary>Photo-tags list query: filter by archival status.</summary>
public sealed class PhotoTagsQueryParams : QueryParams<PhotoTagSortBy>
{
    public ArchivalStatus? Status { get; set; }
}

/// <summary>Albums list query: filter by archival status.</summary>
public sealed class AlbumsQueryParams : QueryParams<PhotoAlbumSortBy>
{
    public ArchivalStatus? Status { get; set; }
}
