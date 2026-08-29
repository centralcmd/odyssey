namespace Odyssey.Dtos.Journal;

/// <summary>
/// Outcome of a vCard import (issue #338). Created/updated contacts succeed and are reported as
/// bare counts; skipped entries are grouped by reason so the client can render a bounded, readable
/// summary — mirrors <c>IcsImportResult</c> (issue #330).
/// </summary>
public sealed record VCardImportResult
{
    public int CreatedCount { get; set; }

    public int UpdatedCount { get; set; }

    public IReadOnlyList<VCardImportSkipGroup> Skipped { get; set; } = [];
}

public sealed record VCardImportSkipGroup
{
    public required string Reason { get; set; }

    public int Count { get; set; }

    // Sample display names for this reason, capped at 100 regardless of file size.
    public IReadOnlyList<string> SampleNames { get; set; } = [];
}
