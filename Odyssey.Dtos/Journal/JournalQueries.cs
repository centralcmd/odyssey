using Odyssey.Dtos;

namespace Odyssey.Dtos.Journal;

// Per-endpoint list-query models for the Journal module (issue #277). Each closes the generic
// QueryParams base over its own sort-key enum (search + SortBy + sort direction + offset/limit) and adds
// only the filters that endpoint exposes, so a list action binds — and passes to its service — the whole
// query as one object. Array filters bind case-insensitively from the matching camelCase query key
// (tagIds → TagIds, statuses → Statuses). These are sealed class rather than record only because they are
// query-string binding models.

/// <summary>Journal-entries list query: filter by tag(s), contact link(s), entry-date range, attachment
/// presence, and archival status.</summary>
public sealed class JournalEntriesQueryParams : QueryParams<JournalEntrySortBy>
{
    public Guid[]? TagIds { get; set; }

    public Guid[]? ContactIds { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    /// <summary>
    /// Restrict to entries that carry at least one photo (<c>true</c>) or none at all (<c>false</c>).
    /// <c>null</c> — the default — does not filter.
    ///
    /// <para>
    /// A nullable <c>bool</c> rather than a flag enum, because it pairs with
    /// <see cref="HasFiles"/> and the two are AND-ed: setting both wants entries carrying both, which
    /// a single "media kind" enum could not express.
    /// </para>
    /// </summary>
    public bool? HasPhotos { get; set; }

    /// <summary>Restrict to entries that carry at least one attachment (<c>true</c>) or none
    /// (<c>false</c>). <c>null</c> does not filter. AND-ed with <see cref="HasPhotos"/>.</summary>
    public bool? HasFiles { get; set; }

    public ArchivalStatus? Status { get; set; }
}

/// <summary>Journal-tags list query: filter by archival status.</summary>
public sealed class JournalTagsQueryParams : QueryParams<JournalTagSortBy>
{
    public ArchivalStatus? Status { get; set; }
}

/// <summary>Tasks list query: filter by tag(s) and lifecycle status(es).</summary>
public sealed class JournalTasksQueryParams : QueryParams<JournalTaskSortBy>
{
    public Guid[]? TagIds { get; set; }

    public JournalTaskStatus[]? Statuses { get; set; }

    /// <summary>Restrict to specific task ids. Used by the .ics export (issue #337) to export a single
    /// task (or a selection) from the row menu; ignored by the list endpoint.</summary>
    public Guid[]? Ids { get; set; }
}

/// <summary>Task-tags list query: filter by archival status.</summary>
public sealed class JournalTaskTagsQueryParams : QueryParams<JournalTaskTagSortBy>
{
    public ArchivalStatus? Status { get; set; }
}
