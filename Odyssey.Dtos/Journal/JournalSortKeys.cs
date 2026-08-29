namespace Odyssey.Dtos.Journal;

// Per-resource allowlisted sort keys for the Journal module list endpoints (issue #277, following the
// Finance ListSortKeys pattern). Each member is a sortable column the resource's list surface exposes;
// the query params bind SortBy as one of these (an unbindable value is rejected, not silently coerced).
// A resource's service orders by the keys it can and stably falls back to its natural default for the rest.

/// <summary>Sortable keys for the journal-entries list.</summary>
public enum JournalEntrySortBy
{
    EntryDate,
    Title,
    CreatedAt,
}

/// <summary>Sortable keys for the journal-tags list.</summary>
public enum JournalTagSortBy
{
    Name,
    Status,
}

/// <summary>Sortable keys for the tasks list.</summary>
public enum JournalTaskSortBy
{
    Position,
    Deadline,
    Title,
    Status,
    CreatedAt,
}

/// <summary>Sortable keys for the task-tags list.</summary>
public enum JournalTaskTagSortBy
{
    Name,
    Status,
}
