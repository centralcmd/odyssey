using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Optional consent payload posted with an analyze request. Analysis sends the complete
/// document to an external AI processor, so the dialog gates the call behind per-document
/// consent and forwards the exact text the user affirmed to be recorded in the audit log.
/// </summary>
public sealed record AnalyzeFileRequest
{
    /// <summary>Whether the user affirmed per-document consent before sending the file.</summary>
    public bool ConsentAcknowledged { get; set; }

    /// <summary>The exact consent text the user affirmed, recorded verbatim.</summary>
    [StringLength(1024)]
    public string? ConsentText { get; set; }

    /// <summary>How consent was captured (defaults to "Per-document checkbox").</summary>
    [StringLength(128)]
    public string? ConsentMethod { get; set; }

    /// <summary>
    /// The <c>disclosureVersion</c> the consent gate rendered, echoed back so the server can check the
    /// user affirmed the disclosure that is still in force (issue #439 §5.3c).
    ///
    /// <para>
    /// The server recomputes it from the same per-run snapshot the transfer uses and answers
    /// <c>409 disclosure_changed</c> on a mismatch, before any job row is created and before any
    /// provider request is made. A <strong>missing or empty</strong> value is a mismatch, not a skip: a
    /// client that sends none has not shown the user a disclosure this server can vouch for.
    /// </para>
    ///
    /// <para>
    /// No <c>[Required]</c>: the rejection is a <c>409</c> with a machine-readable code the dialog acts
    /// on by re-prompting, not a <c>400</c> that reads as a malformed request.
    /// </para>
    /// </summary>
    [StringLength(64)]
    public string? DisclosureVersion { get; set; }
}
