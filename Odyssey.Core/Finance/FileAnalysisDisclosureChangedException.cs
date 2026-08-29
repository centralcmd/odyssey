using System.Net;
using Odyssey.Core;

namespace Odyssey.Core.Finance;

/// <summary>
/// The <c>disclosureVersion</c> the consent gate rendered no longer matches the disclosure in force
/// (issue #439 §5.3c). Maps to <c>409 Conflict</c> with the machine-readable code the dialog switches
/// on.
///
/// <para>
/// The gap it closes: <c>FileAnalysisDisclosureCache</c> on the client is stale-while-revalidate, and
/// the invalidation a settings save triggers reaches only the <em>administrator's</em> browser. Once
/// the processor, region, model and destination are all runtime-editable, a user can open the gate on
/// a cached disclosure, affirm "…consent to sending the complete file to Anthropic…", and have the
/// server transfer to <c>gateway.internal</c> moments later — a job whose recorded host and whose
/// consent sentence name different recipients, with nothing flagging it.
/// </para>
///
/// <para>
/// A mismatch is raised <strong>before any job row is created and before any provider request is
/// made</strong>, and a missing or empty version counts as a mismatch: a client that sends none has
/// not shown the user a disclosure this server can vouch for, and defaulting to "allow" would make the
/// whole check opt-out by omission.
/// </para>
///
/// <para>
/// It is evaluated <em>after</em> the disabled and configuration-unavailable checks, so a disabled or
/// misconfigured instance never leaks disclosure state through a <c>409</c>.
/// </para>
/// </summary>
public sealed class FileAnalysisDisclosureChangedException : DomainException
{
    public const string DisclosureChangedCode = "disclosure_changed";

    public override int StatusCode => (int)HttpStatusCode.Conflict;

    public override string? Code => DisclosureChangedCode;

    public FileAnalysisDisclosureChangedException()
        : base("The details of who processes your document changed. Please review them again before continuing.")
    {
    }
}
