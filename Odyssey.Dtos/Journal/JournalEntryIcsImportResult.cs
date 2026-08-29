namespace Odyssey.Dtos.Journal;

/// <summary>
/// Outcome of importing a <c>VJOURNAL</c> <c>.ics</c> file (issue #339). Mirrors
/// <see cref="TaskIcsImportResult"/>: entry-level create/update counts plus per-reason skip groups, and
/// four link-level skip counts for references that couldn't be resolved (unmatched tags, unresolved or
/// claim-gated contacts, unresolvable attachments/photos) without failing the entry itself. No
/// recurrence flag exists here — journal entries have no recurrence concept (§2 Non-Goal 1).
/// </summary>
public sealed record JournalEntryIcsImportResult
{
    public int ImportedCount { get; set; }

    public int UpdatedCount { get; set; }

    public IReadOnlyList<JournalEntryImportSkipGroup> Skipped { get; set; } = [];

    public int SkippedTagLinkCount { get; set; }

    public int SkippedContactLinkCount { get; set; }

    public int SkippedAttachmentCount { get; set; }

    public int SkippedPhotoCount { get; set; }
}

/// <summary>A group of blocks skipped for the same reason, with a capped list of sample titles.</summary>
public sealed record JournalEntryImportSkipGroup
{
    public required string Reason { get; set; }

    public int Count { get; set; }

    public IReadOnlyList<string> SampleTitles { get; set; } = [];
}
