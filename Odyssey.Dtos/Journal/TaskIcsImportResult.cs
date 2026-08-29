namespace Odyssey.Dtos.Journal;

/// <summary>
/// Outcome of a VTODO/.ics task import (issue #337). Shaped after
/// <c>Odyssey.Dtos.Journal.IcsImportResult</c>: created/updated tasks are reported as bare counts,
/// skipped components are grouped by reason so the client renders a bounded, readable summary, and the
/// two link counters surface partially-applied tasks (a task imported but with some tag/attachment
/// references dropped). There is no <c>AnySeriesRegenerated</c> counterpart — recurring VTODOs are
/// rejected outright (§2 Non-Goal 2), never partially regenerated.
/// </summary>
public sealed record TaskIcsImportResult
{
    public int ImportedCount { get; set; }

    public int UpdatedCount { get; set; }

    public IReadOnlyList<TaskImportSkipGroup> Skipped { get; set; } = [];

    /// <summary>CATEGORIES values across all components that didn't resolve to an existing, non-archived tag.</summary>
    public int SkippedTagLinkCount { get; set; }

    /// <summary>odyssey-file: ATTACH URIs that didn't resolve to a file the importing user may link.</summary>
    public int SkippedAttachmentCount { get; set; }
}

/// <summary>A set of components skipped for the same reason, with a bounded sample of their titles.</summary>
public sealed record TaskImportSkipGroup
{
    public required string Reason { get; set; }

    public int Count { get; set; }

    // Sample task titles for this reason, capped at 100 regardless of file size.
    public IReadOnlyList<string> SampleTitles { get; set; } = [];
}
