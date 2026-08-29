namespace Odyssey.Dtos.Application;

/// <summary>
/// What the analyze-file consent gate must tell the user before their document is transferred to a
/// third party (issue #421 Wave 1) — GDPR Art. 13.
///
/// <para>
/// Deliberately narrower than the admin-gated <c>SystemSettingsDto</c>: five values and nothing else,
/// so a claim-free read cannot leak the security toggles, the volume caps, the sender identity or the
/// administrator who last changed a setting. Same shape of decision as <c>ImportLimitsDto</c>.
/// </para>
///
/// <para>
/// These existed twice before this — server-side on <c>FileAnalysisOptions</c> and again as
/// compile-time constants in the Blazor client — and had already drifted: the panel named a model the
/// server did not use. One authoritative source now, fetched rather than compiled in.
/// </para>
/// </summary>
public sealed record FileAnalysisDisclosureDto
{
    /// <summary>The third party the document is sent to.</summary>
    public string Processor { get; set; } = string.Empty;

    /// <summary>Where that processing happens — GDPR Chapter V.</summary>
    public string ProcessorRegion { get; set; } = string.Empty;

    /// <summary>The lawful basis recorded against the transfer.</summary>
    public string LawfulBasis { get; set; } = string.Empty;

    /// <summary>Absolute https URL, re-validated on this read path before being served.</summary>
    public string PrivacyNoticeUrl { get; set; } = string.Empty;

    /// <summary>
    /// The model the server will actually call. Sourced from the settings store since issue #439 —
    /// the exposure is unchanged, only where the value comes from.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Whether AI document analysis is switched on for this instance (issue #439), from the
    /// <strong>live</strong> read rather than the cached snapshot.
    ///
    /// <para>
    /// It exists so the Analyze affordance can render disabled with an explanation instead of letting
    /// a user pick a document, read the consent gate, affirm it and only then receive a <c>503</c> —
    /// a poor sequence for a consent interaction, and one that becomes reachable at runtime now that
    /// the switch is editable. No consent is collected for a transfer that cannot happen.
    /// </para>
    ///
    /// <para>
    /// A boolean about feature availability carries no infrastructure detail, which is why it is safe
    /// on this claim-free response — unlike the base URL, which is deliberately absent from it.
    /// </para>
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// An integrity token over the exact disclosure this response carries, echoed back on analyze so
    /// the server can refuse a consent affirmed against facts that have since changed (issue #439
    /// §5.3c). See <c>FileAnalysisDisclosureVersion</c> for the input tuple and why <c>enabled</c> is
    /// excluded from it.
    /// </summary>
    public string DisclosureVersion { get; set; } = string.Empty;
}
