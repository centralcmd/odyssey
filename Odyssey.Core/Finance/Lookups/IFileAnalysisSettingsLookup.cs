namespace Odyssey.Core.Finance;

/// <summary>
/// The AI file-analysis policy and processor-disclosure values migrated off <c>appsettings.json</c>
/// into the database-backed system-settings store (issue #421 Wave 1).
/// </summary>
/// <param name="Processor">Third party the document is transferred to. GDPR Art. 13(1)(e).</param>
/// <param name="ProcessorRegion">Where that processing happens — GDPR Chapter V.</param>
/// <param name="LawfulBasis">Recorded verbatim on each job's audit row.</param>
/// <param name="PrivacyNoticeUrl">Already canonicalised and https-verified by the read path.</param>
/// <param name="MaxFutureTransactionDays">How far ahead an extracted transaction date may fall.</param>
/// <param name="AutoLinkThreshold">
/// Confidence at or above which a match is auto-linked. <strong>Higher is safer</strong> — the matcher
/// applies <c>confidence &gt;= threshold</c>, so a larger value auto-links less. That is why a degraded
/// read resolves this one <em>upward</em> while every other bound-like setting resolves downward.
/// </param>
/// <param name="MaxTokens">
/// Model output cap on both the extraction and match calls (issue #434 key 1). Bounds extraction
/// completeness <em>and</em> per-request provider spend, which is why it carries the security claim
/// and is stamped on each job.
/// </param>
/// <param name="MatchMaxVocabulary">
/// Per-list vocabulary cap for the match step. Over the cap the LLM match is skipped, not truncated.
/// </param>
/// <param name="MatchTimeoutSeconds">
/// Hard per-match-call timeout. On timeout the job records <c>MatchStatus = Failed</c> and the
/// candidates stay importable by hand.
/// </param>
/// <param name="Model">
/// The model each analysis runs on and is stamped with (issue #439).
///
/// <para>
/// <strong>Nullable on purpose, and the nullability is the guarantee.</strong> <c>null</c> means the
/// stored value could not be used — the row is blank, or the read failed. It is never a substituted
/// default: substituting one would stamp <c>FileAnalysisJob.AnalyzerModel</c> with a model that did not
/// run, which is the provenance corruption issue #421 Non-Goal 6 was protecting against and the one
/// mechanism by which the audit trail could go wrong.
/// </para>
///
/// <para>
/// Why not a <c>string</c> plus a flag: <see cref="FileAnalysisSettings.IsDegraded"/> is a single
/// boolean threaded through every resolver, so it cannot say <em>which</em> field degraded. A rule
/// phrased as "refuse when the degradation touched Model or BaseUrl" is not expressible against it, and
/// resolving that ambiguity lands on one of two wrong answers — refuse on any degradation (so a blank
/// processor row blocks all analysis) or invent a value comparison (which stops firing the moment an
/// administrator deliberately sets a value back to its default). Nullability moves the guarantee out of
/// a behavioural rule and into the type: <see cref="FileAnalysisTarget"/> cannot be constructed from a
/// null, so "never substitute" holds by construction rather than by an assertion.
/// </para>
/// </param>
/// <param name="BaseUrl">
/// Where analysis requests go — scheme and host only, re-validated on the read path exactly as
/// <paramref name="PrivacyNoticeUrl"/> is, because a row planted by a restore or a hand edit never met
/// the write validator. Nullable for the same reason as <paramref name="Model"/>, with a sharper
/// consequence: falling back to <c>api.anthropic.com</c> would transfer a document to a processor
/// neither the administrator nor the consenting user chose, contradicting the disclosure affirmed.
/// </param>
/// <param name="IsDegraded">
/// True when any value fell back rather than being read cleanly. The claim-free disclosure endpoint
/// returns <c>503</c> on this rather than presenting a fallback as authoritative — the same reason
/// <c>ImportLimitsController</c> does, and a stronger one here, since this is legal disclosure text.
/// </param>
public sealed record FileAnalysisSettings(
    string Processor,
    string ProcessorRegion,
    string LawfulBasis,
    string PrivacyNoticeUrl,
    int MaxFutureTransactionDays,
    decimal AutoLinkThreshold,
    int MaxTokens,
    int MatchMaxVocabulary,
    int MatchTimeoutSeconds,
    string? Model,
    string? BaseUrl,
    bool IsDegraded);

/// <summary>
/// Where one analysis call goes and what it runs on, resolved once per run from the settings snapshot
/// (issue #439).
///
/// <para>
/// A parameter rather than something the provider reads for itself, for the reason
/// <c>IFileAnalysisProvider</c>'s remarks already give for <c>maxTokens</c>: the service takes one
/// snapshot per run and stamps it on the job, so passing the target down is what guarantees the value a
/// request was <em>built with</em> is the value the audit record reports. Reading it inside the provider
/// would let a concurrent administrator write separate the two.
/// </para>
///
/// <para>
/// Both members are non-nullable, which is the point: a degraded read yields a null on
/// <see cref="FileAnalysisSettings"/>, this record cannot be constructed from it, and the analysis
/// refuses instead of silently substituting a default.
/// </para>
/// </summary>
/// <param name="BaseUrl">Absolute https, scheme and authority only — the provider appends the path.</param>
/// <param name="Model">Stamped verbatim on the job as <c>AnalyzerModel</c>.</param>
public sealed record FileAnalysisTarget(string BaseUrl, string Model);

/// <summary>
/// Narrow cross-domain lookup for the file-analysis settings (issue #421 Wave 1).
///
/// <para>
/// A separate interface from <see cref="ISystemSettingsLookup"/> rather than an extra method on it.
/// The load-bearing reason is the project boundary — a lookup interface lives in the domain project
/// that consumes it, so <c>Odyssey.Core.Tests</c> can fake it without referencing
/// <c>Odyssey.Context</c>. (A secondary reason: <see cref="ISystemSettingsLookup"/>'s
/// implementation shares one cache entry with the Insurance eviction path, so folding these in would
/// make an insurance save evict the analysis settings.)
/// </para>
/// </summary>
public interface IFileAnalysisSettingsLookup
{
    Task<FileAnalysisSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The kill switch, read <strong>live on every call</strong> — never cached, and deliberately not a
    /// member of <see cref="FileAnalysisSettings"/> so no caller can consume a cached copy by accident
    /// (issue #439 §5.1).
    ///
    /// <para>
    /// The snapshot's 30-second TTL is fine for policy values and wrong for this one. "I turned it off"
    /// has to mean the next request is refused, not that the next request within 30 seconds may still
    /// transfer a document to a third party — and the snapshot's eviction is instance-local, so on a
    /// multi-instance deployment it would not even bound the window to the TTL everywhere. This follows
    /// the <c>RegistrationRequireAdminApproval</c> / <c>EmailRequireConfirmation</c> perimeter
    /// precedent instead: one single-row primary-key read per file-analysis service call, on paths that
    /// each already do at least one round trip and, on analyze, a multi-second provider call.
    /// </para>
    ///
    /// <para>
    /// <strong>A read fault resolves to <see langword="false"/>.</strong> Fail closed: a failed settings
    /// read must never be the reason a document leaves the deployment.
    /// </para>
    /// </summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);
}
