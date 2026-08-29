namespace Odyssey.Core.Journal.Interop;

/// <summary>
/// Shared validation bounds for the ICS/vCard import pipelines (Calendar/JournalEntry/Task/Contact,
/// architect finding F-9), for the two that are genuinely compile-time and the file-URI scheme.
///
/// <para>
/// What used to live here and no longer does: <c>MaxSamplesPerSkipReason</c> became
/// <c>ImportMaxSamplesPerSkipReason</c> and <c>MaxLinksPerKind</c> was deleted outright, both in issue
/// #434. The link cap is the more important of the two — it was a <em>duplicate</em> of the
/// <c>JournalEntryMaxLinksPerKind</c>/<c>JournalTaskMaxLinksPerKind</c> settings that had been
/// admin-editable since #421 Wave 3, so an administrator who lowered either one saw it honoured on the
/// create/update path and silently ignored on the ICS import path (§9-A, defect A).
/// </para>
///
/// <para>
/// The two lengths below stay compiled on purpose (Non-Goal 6): they are wire-format validation bounds
/// matched to the entity column widths, so raising one past its column length would push the failure
/// from a clean per-row import skip to a <c>DbUpdateException</c>. <see cref="FileUriScheme"/> is a
/// format identifier, not a limit.
/// </para>
/// </summary>
internal static class ImportLimits
{
    /// <summary>Max title length accepted on import; a longer title is skipped, not truncated.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Max description/content length accepted on import; a longer body is skipped, not
    /// truncated.</summary>
    public const int MaxContentLength = 4096;

    /// <summary>URI scheme Odyssey emits/recognizes for a file attachment reference (<c>ATTACH</c>).</summary>
    public const string FileUriScheme = "odyssey-file";
}
