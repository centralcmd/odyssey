namespace Odyssey.Dtos.Journal;

/// <summary>
/// What a VTODO/.ics task export would contain, computed by the same checks the export itself runs. The client asks for
/// it before starting the download, so a refusal (over a cap, or attachments the caller may not read) arrives as an
/// ordinary error instead of a failed browser download.
/// </summary>
public sealed record TaskIcsExportSummary
{
    public int TaskCount { get; set; }

    /// <summary>Attached files whose content travels inline in the export.</summary>
    public int AttachmentCount { get; set; }

    /// <summary>The attached files' combined size before encoding.</summary>
    public long AttachmentBytes { get; set; }
}
