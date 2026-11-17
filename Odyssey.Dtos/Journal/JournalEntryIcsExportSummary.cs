namespace Odyssey.Dtos.Journal;

/// <summary>
/// What a VJOURNAL/.ics journal export would contain, computed by the same checks the export itself runs. The client
/// asks for it before starting the download, so a refusal (a cap, or attached files or photos the caller may not read)
/// arrives as an ordinary error instead of a failed browser download.
/// </summary>
public sealed record JournalEntryIcsExportSummary
{
    public int EntryCount { get; set; }

    /// <summary>Attached files whose content travels inline in the export.</summary>
    public int AttachmentCount { get; set; }

    /// <summary>Linked library photos whose content travels inline in the export.</summary>
    public int PhotoCount { get; set; }

    /// <summary>The attached files' and photos' combined size before encoding.</summary>
    public long AttachmentBytes { get; set; }
}
