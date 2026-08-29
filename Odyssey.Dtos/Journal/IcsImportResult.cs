namespace Odyssey.Dtos.Journal;

/// <summary>
/// Outcome of an ICS import (issue #330). Imported/updated events succeeded and are reported as bare
/// counts; skipped events are grouped by reason so the client can render a bounded, readable summary.
/// </summary>
public sealed record IcsImportResult
{
    public int ImportedCount { get; set; }

    public int UpdatedCount { get; set; }

    // True when at least one UID-matched pattern update regenerated future occurrences, discarding any
    // individually-edited future rows (exactly as editing a series through the calendar UI does). The
    // client surfaces this as an explicit warning rather than a bare "updated" count.
    public bool AnySeriesRegenerated { get; set; }

    public IReadOnlyList<IcsImportSkipGroup> Skipped { get; set; } = [];
}

public sealed record IcsImportSkipGroup
{
    public required string Reason { get; set; }

    public int Count { get; set; }

    // Sample event titles for this reason, capped at 100 regardless of file size.
    public IReadOnlyList<string> SampleTitles { get; set; } = [];
}
