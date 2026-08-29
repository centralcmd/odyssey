namespace Odyssey.Dtos.Journal;

// Per-resource allowlisted sort keys for the Contacts module list endpoint (issue #325, following
// the Finance ListSortKeys pattern). Each member is a sortable column the resource's list surface
// exposes; the query params bind SortBy as one of these (an unbindable value is rejected, not
// silently coerced).

/// <summary>Sortable keys for the contacts list.</summary>
public enum ContactSortBy
{
    Name,
    Type,
    NormalizedName,
    Status,
}
