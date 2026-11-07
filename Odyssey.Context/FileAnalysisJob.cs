using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(AccountFileId))]
[Index(nameof(Status))]
[Index(nameof(RequestedByUserId))]
public class FileAnalysisJob
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    // FK to AccountFile enforces that the file belongs to the account — prevents cross-account data leaks.
    [Required]
    public required Guid AccountFileId { get; set; }

    [ForeignKey(nameof(AccountFileId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public AccountFile? AccountFile { get; set; }

    [Required]
    public FileAnalysisJobStatus Status { get; set; } = FileAnalysisJobStatus.New;

    [StringLength(256)]
    public string? FileTypeDetected { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    [StringLength(128)]
    public string? FailureCode { get; set; }

    [StringLength(1024)]
    public string? FailureMessage { get; set; }

    [Required]
    public AnalyzerProvider AnalyzerProvider { get; set; } = AnalyzerProvider.None;

    [StringLength(256)]
    public string? AnalyzerModel { get; set; }

    [StringLength(128)]
    public string? PromptVersion { get; set; }

    public string? RequestedByUserId { get; set; }

    // ── Privacy gate accountability (ISO 27001 / GDPR Art. 6) ─────────────────
    // Analysis sends the complete document to an external AI processor, so each
    // transfer carries the per-document consent the user affirmed and the lawful
    // basis under which it was sent. Recorded verbatim so the admin audit log
    // shows exactly what the user agreed to.

    /// <summary>Whether the user affirmed per-document consent before the transfer.</summary>
    public bool ConsentRecorded { get; set; }

    /// <summary>How consent was captured (e.g. "Per-document checkbox").</summary>
    [StringLength(128)]
    public string? ConsentMethod { get; set; }

    /// <summary>The exact consent text the user affirmed, stored verbatim.</summary>
    [StringLength(1024)]
    public string? ConsentText { get; set; }

    /// <summary>Lawful basis surfaced in the gate and recorded with the transfer (GDPR Art. 6).</summary>
    [StringLength(128)]
    public string? LawfulBasis { get; set; }

    // ── AI matching step (issue #266) ───────────────────────────────────────────
    // Orthogonal to extraction Status: the extraction Status still governs whether
    // candidates are importable; MatchStatus only governs whether suggestions exist.

    /// <summary>The AI match step's outcome, independent of the extraction <see cref="Status"/>.</summary>
    [Required]
    public FileAnalysisMatchStatus MatchStatus { get; set; } = FileAnalysisMatchStatus.NotRun;

    /// <summary>A curated reason when <see cref="MatchStatus"/> is Failed — never the raw provider body.</summary>
    [StringLength(1024)]
    public string? MatchFailureMessage { get; set; }

    /// <summary>How many names were sent to the provider for the match (audit signal).</summary>
    public int? VocabularyCount { get; set; }

    /// <summary>
    /// The auto-link confidence threshold in force when this job's matches were applied (issue #421
    /// Wave 1).
    ///
    /// <para>
    /// Every other field on the read DTO comes from the persisted job, but the threshold used to be
    /// read from live options — harmless while it was a compile-time constant, and wrong the moment an
    /// admin can change it: the client re-derives "auto-linked" versus "suggested" from this value, so
    /// editing it would silently re-interpret the stored confidences of jobs that already completed.
    /// </para>
    ///
    /// <para>
    /// <strong>Nullable on purpose.</strong> Jobs created before this column existed were never matched
    /// under a recorded threshold, and back-filling a default would assert a threshold they did not
    /// run under. A null reads as "fall back to the live value", which is exactly the old behaviour,
    /// preserved for old rows.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <c>Precision(5, 4)</c> to match the confidence columns it is compared against
    /// (<c>FileAnalysisCandidateTransaction.MerchantMatchConfidence</c>, <c>LlmConfidence</c>) — a
    /// threshold stored at coarser precision than the values it gates would round the boundary case.
    /// </remarks>
    [Precision(5, 4)]
    public decimal? AutoLinkThresholdInForce { get; set; }

    /// <summary>
    /// The model output cap this job ran under (issue #434 §8), joining
    /// <see cref="AutoLinkThresholdInForce"/> on the same provenance argument: <c>MaxTokens</c> bounds
    /// the model's output and therefore bounds extraction <em>completeness</em>, so without it a
    /// truncated extraction from six months ago is indistinguishable from a model failure.
    ///
    /// <para>
    /// <strong>Nullable, no default, no backfill.</strong> A null reads as "ran before the stamp
    /// existed", which is exactly right and is not a value.
    /// </para>
    /// </summary>
    public int? MaxTokensInForce { get; set; }

    /// <summary>
    /// The per-match-call timeout this job's match step ran under (issue #434 §8). Nullable for the
    /// same reason as <see cref="MaxTokensInForce"/>, and null for any job whose match step never ran.
    /// </summary>
    public int? MatchTimeoutSecondsInForce { get; set; }

    // ── Transfer provenance (issue #439) ───────────────────────────────────────
    // AnalyzerModel, PromptVersion, LawfulBasis, AutoLinkThresholdInForce and MaxTokensInForce
    // already record the conditions a job ran under. Once the RECIPIENT of the personal data became
    // admin-editable, the recipient joined those conditions: GDPR Art. 30(1)(e) record-keeping asks
    // where personal data went, and the answer is no longer "whatever the deployment config said".
    //
    // All three are written ONCE at job creation from the SAME per-run settings snapshot as the
    // lawful basis and the threshold, and are immutable thereafter — so they describe one coherent
    // moment rather than a mixture. Together they let an auditor show what the deployment asserted at
    // the instant of transfer, and detect a job whose consent text and recorded processor disagree.
    //
    // All three are nullable with NO BACKFILL. Pre-existing jobs genuinely have no recorded
    // destination, processor or region, and inventing one — even the then-configured default — would
    // put a value into an audit record that was never observed. That matters most for the region,
    // where a fabricated value would be a fabricated answer to "was this a third-country transfer?".

    /// <summary>
    /// The HOST analysis requests for this job were sent to.
    ///
    /// <para>
    /// Host only — never the path, query or <c>userinfo</c>, which can carry credentials on a gateway
    /// URL. With redirects disabled on the outbound client, this cannot diverge from where the data
    /// actually went; that is the pairing which makes it a record rather than a restatement of config.
    /// </para>
    ///
    /// <para>Null on jobs recorded before this column existed. Exposed on the admin audit surface only.</para>
    /// </summary>
    [StringLength(256)]
    public string? AnalyzerBaseUrlHost { get; set; }

    /// <summary>
    /// The processor disclosed at the moment of this transfer. It survived only <em>incidentally</em>
    /// before — by being interpolated into the composed consent sentence — which is not a record.
    /// </summary>
    [StringLength(128)]
    public string? ProcessorInForce { get; set; }

    /// <summary>
    /// The processing region disclosed at the moment of this transfer. This is the fact that decides
    /// whether the transfer was a third-country transfer under GDPR Art. 44–49, and before this column
    /// it was recorded nowhere at all — despite having been admin-editable since issue #421.
    /// </summary>
    [StringLength(128)]
    public string? ProcessorRegionInForce { get; set; }

    // ── Match vocabulary and administrator guidance (issue #433) ─────────────────────────────────

    /// <summary>
    /// What the user's consent covered for the match step, written once at analyze. <c>NULL</c> is a
    /// job created before the feature, matched names-only for its whole life, re-matches included.
    /// </summary>
    public Odyssey.Dtos.Finance.FileAnalysisMatchVocabularyScope? MatchVocabularyScope { get; set; }

    /// <summary>The full disclosure version the user consented to, written at analyze.</summary>
    [StringLength(16)]
    public string? DisclosureVersionInForce { get; set; }

    /// <summary>
    /// The match-relevant part of that disclosure, written at analyze from the same snapshot. Match
    /// compares a fresh value against it and refuses on a difference, so a match never sends data
    /// under a disclosure the user did not see. It excludes the extraction-guidance flag: extraction has
    /// already run, so changing that guidance must not refuse matches on open analyses.
    /// </summary>
    [StringLength(16)]
    public string? MatchDisclosureVersionInForce { get; set; }

    /// <summary>The administrator's extraction guidance this run sent, verbatim; <c>NULL</c> when none.</summary>
    // TEXT, not varchar(4000): two utf8mb4 varchar(4000) columns would take 32 000 bytes of the table's
    // 65 535-byte row-size budget. The 4000 limit is the rule's, enforced on write.
    [StringLength(4000)]
    [Column(TypeName = "text")]
    public string? ExtractionPromptAdditionInForce { get; set; }

    /// <summary>The match guidance the latest match run sent, verbatim; <c>NULL</c> when none.</summary>
    // TEXT, not varchar(4000): two utf8mb4 varchar(4000) columns would take 32 000 bytes of the table's
    // 65 535-byte row-size budget. The 4000 limit is the rule's, enforced on write.
    [StringLength(4000)]
    [Column(TypeName = "text")]
    public string? MatchPromptAdditionInForce { get; set; }

    /// <summary>
    /// The version of the match text the latest run actually sent. A names-only run sends the
    /// pre-feature text and is stamped <c>1.0</c>; <see cref="PromptVersion"/> is stamped at analyze
    /// and says nothing about the match text.
    /// </summary>
    [StringLength(32)]
    public string? MatchPromptVersion { get; set; }

    /// <summary>Contacts the latest match run sent.</summary>
    public int? ContactCount { get; set; }

    /// <summary>Tags the latest match run sent.</summary>
    public int? TagCount { get; set; }

    /// <summary>Contact aliases the latest match run sent, after the payload budget.</summary>
    public int? AliasCount { get; set; }

    /// <summary>Tag descriptions the latest match run sent, after the payload budget.</summary>
    public int? TagDescriptionCount { get; set; }

    /// <summary>How far the latest match run had to shrink its vocabulary to fit the payload budget.</summary>
    public Odyssey.Dtos.Finance.FileAnalysisVocabularyReduction? VocabularyReduction { get; set; }

    /// <summary>
    /// The match claim's concurrency token. The running guard is check-then-save, so on its own two
    /// simultaneous requests can both pass it; every claim and every match-status write increments
    /// this, and the loser's save fails instead of sending a second time. The same pattern as the
    /// <c>Generation</c> tokens on <c>PhotoThumbnailJobs</c> and <c>JobRuns</c>.
    /// </summary>
    [ConcurrencyCheck]
    public int MatchGeneration { get; set; }

    public ICollection<FileAnalysisCandidateTransaction> CandidateTransactions { get; set; } = new List<FileAnalysisCandidateTransaction>();
}
