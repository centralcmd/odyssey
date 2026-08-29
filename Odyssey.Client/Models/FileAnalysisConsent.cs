namespace Odyssey.Client.Models;

/// <summary>
/// The wording behind the analyze-file consent gate (issue #421 Wave 1).
///
/// <para>
/// The four processor facts used to be compile-time constants here, duplicated from the server and
/// already drifted — the panel named a model the server did not use. They now come from
/// <c>GET /api/file-analysis/disclosure</c>, and the last-resort copy lives in
/// <c>FileAnalysisDisclosureCache.Fallback</c>.
/// </para>
///
/// <para>
/// <see cref="Compose"/> replaces what was a frozen sentence naming Anthropic. Composing it from the
/// same disclosure values the panel renders makes panel and affirmation unable to disagree by
/// construction; consent-record integrity is preserved instead by the dialog sending the
/// <em>rendered</em> sentence, which the server stores verbatim on the job. Freezing the template
/// preserved <em>a</em> wording; persisting the rendered one preserves <em>the wording shown</em>.
/// </para>
/// </summary>
public static class FileAnalysisConsent
{
    /// <summary>How the consent was collected, as recorded on the audit entry.</summary>
    public const string Method = "Per-document checkbox";

    /// <summary>
    /// The sentence the reviewer affirms, for a given processor. Deliberately processor-agnostic
    /// phrasing: an English possessive ("Anthropic's Claude API") reads badly for any processor name
    /// that is not a bare English noun, and the value is admin-editable.
    /// </summary>
    public static string Compose(string processor) =>
        $"I\u2019m authorized to share this document and consent to sending the complete file to {processor} for analysis.";
}

/// <summary>
/// A contact staged optimistically by the review grid and still awaiting its server-side create.
/// </summary>
/// <param name="TempId">The client-side id linked to the row until the real one arrives.</param>
/// <param name="Name">The typed (or extracted) name to create the contact under.</param>
public sealed record FileAnalysisPendingContact(Guid TempId, string Name);
