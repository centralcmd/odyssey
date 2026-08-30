namespace Odyssey.Dtos;

/// <summary>
/// The shipped default <em>value</em> of every admin-configurable system setting, plus the
/// <see cref="Unlimited"/> sentinel a count setting stores to mean "no limit".
///
/// <para>
/// <strong>Why the values live here and the keys do not</strong> (issue #434 §5 item 0). A
/// single-direction setting pins one end of its <c>[Range]</c> at its shipped default — key 11 and
/// key 12 are tighten-only, so their range <em>maximum</em> is the default; key 14 is raise-only, so
/// its range <em>minimum</em> is. An attribute argument must be a compile-time constant naming a
/// real symbol, and <c>SystemSettingsUpdate</c> lives in <c>Odyssey.Dtos.Application</c>, whose only
/// project reference is this assembly. <c>Odyssey.Context</c> — where
/// <c>SystemSettingsKeys</c> lives — references <c>Odyssey.Dtos.Application</c>, so the edge the
/// attribute would need is a <em>cycle</em>, not merely a missing reference. Putting the numbers in
/// this project (zero project references, referenced by eight) lets the seed, the write DTO's bound
/// and the client catalogue all name the same symbol.
/// </para>
///
/// <para>
/// This is the same vocabulary/mapping split <c>CLAUDE.md</c> documents for
/// <see cref="Authorization.PermissionClaims"/>: the shared half carries values everybody needs,
/// while the server-only half (<c>SystemSettingsKeys</c>) keeps the key strings, <c>AllKeys</c> and
/// the parse/format helpers.
/// </para>
/// </summary>
public static class SystemSettingsDefaults
{
    /// <summary>
    /// The single literal sentinel a "count" setting's stored value carries to mean "no limit" —
    /// every reader parses/formats it through <c>SystemSettingsKeys.ParseCount</c>/
    /// <c>FormatCount</c> rather than re-testing the literal itself (issue #343 arch 3).
    /// </summary>
    public const string Unlimited = "unlimited";

    // ── The three authentication-perimeter toggles and the two insurance knobs (issue #349) ──────

    public const bool RequireTwoFactor = false;
    public const bool RegistrationRequireAdminApproval = true;
    public const bool EmailRequireConfirmation = true;
    public const int InsuranceExpiringSoonWindowDays = 30;
    public const int InsuranceMaxSummaryPolicies = 1000;

    // ── Import/export volume caps (issue #343 and follow-ups) ────────────────────────────────────
    //
    // Seeded values mirror today's effective behavior exactly. The two vCard count caps are seeded
    // "unlimited" (today's effective int.MaxValue); the three ICS surfaces keep their existing
    // 2,000-derived count defaults. All eight size (MB) defaults — import and export, all four
    // surfaces — were later unified to a single 64 MB out-of-the-box value, per product decision.

    public const string ContactVCardMaxExportRows = Unlimited;
    public const string ContactVCardMaxImportEntries = Unlimited;
    public const int ContactVCardMaxImportMegabytes = 64;
    public const int ContactVCardMaxExportMegabytes = 64;
    public const int CalendarIcsMaxExportEvents = 2000;
    public const int CalendarIcsMaxImportEvents = 2000;
    public const int CalendarIcsMaxImportMegabytes = 64;
    public const int CalendarIcsMaxExportMegabytes = 64;
    public const int TaskIcsMaxExportTasks = 2000;
    public const int TaskIcsMaxImportTasks = 2000;
    public const int TaskIcsMaxImportMegabytes = 64;
    public const int TaskIcsMaxExportMegabytes = 64;
    public const int JournalIcsMaxExportRows = 2000;
    public const int JournalIcsMaxImportEntries = 2000;
    public const int JournalIcsMaxImportMegabytes = 64;
    public const int JournalIcsMaxExportMegabytes = 64;

    // ── AI file-analysis policy and processor disclosure (issue #421 Wave 1) ─────────────────────
    //
    // These mirror today's effective behaviour, with one deliberate correction:
    // MaxFutureTransactionDays was 90 in appsettings.json and 30 on FileAnalysisOptions, and 90 is
    // what actually ran.

    public const string FileAnalysisProcessor = "Anthropic";
    public const string FileAnalysisProcessorRegion = "United States";
    public const string FileAnalysisLawfulBasis = "Consent · GDPR Art. 6(1)(a)";
    public const string FileAnalysisPrivacyNoticeUrl = "https://www.anthropic.com/legal/privacy";
    public const int FileAnalysisMaxFutureTransactionDays = 90;

    /// <summary>
    /// Auto-link confidence threshold. <strong>Higher is safer</strong> — the matcher auto-links when
    /// <c>confidence &gt;= threshold</c>, so a larger value auto-links less. That direction is why a
    /// degraded read of this setting must resolve to the <em>maximum</em> of last-known-good and
    /// default, not the minimum (issue #421 §5); the first draft of that spec had it backwards.
    /// </summary>
    public const decimal FileAnalysisMatchAutoLinkThreshold = 0.60m;

    // ── Transactional email (issue #421 Wave 2), mirroring today's shipped values ────────────────

    public const string EmailFromAddress = "no-reply@odyssey.local";
    public const string EmailFromName = "Odyssey";

    /// <summary>
    /// Messages allowed to one recipient address per <see cref="EmailPerRecipientWindowMinutes"/>.
    /// The conservative direction for a degraded read is <strong>down</strong> for this one but
    /// <strong>up</strong> for the window — a longer window is a tighter throttle (issue #421 §5).
    /// </summary>
    public const int EmailPerRecipientLimit = 3;

    public const int EmailPerRecipientWindowMinutes = 60;

    // ── Per-request defensive caps (issue #421 Wave 3), mirroring the POCO/const values ──────────
    // Every one is a cap, so the conservative direction for a degraded read is min for all nine.

    public const int ContractMaxPartiesPerContract = 25;
    public const int ContractMaxFilesPerContract = 50;
    public const int ContractMaxSummaryContracts = 1000;
    public const int InsuranceMaxRenewalsPerPolicy = 100;
    public const int InsuranceMaxFilesPerParent = 50;
    public const int PhotoMaxLinksPerKind = 50;
    public const int PhotoMaxAlbumMembers = 1000;
    public const int JournalEntryMaxLinksPerKind = 50;
    public const int JournalTaskMaxLinksPerKind = 50;

    /// <summary>
    /// The shipped upload cap, in megabytes. Matches the 64 MB that
    /// <c>FileStorage:MaxFileSizeBytes</c> pins in <c>appsettings.json</c>, so a default install sees
    /// no behaviour change. Conservative direction for a degraded read is <c>min</c> — a smaller
    /// upload permitted.
    /// </summary>
    public const int FileStorageMaxUploadMegabytes = 64;

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // The last compiled-in tuning constants (issue #434). Every value below mirrors exactly what the
    // code did before it became editable, so a default install is behaviourally identical.
    //
    // Three of the fifteen are SINGLE-DIRECTION, and their bound is this constant rather than a
    // second literal that could drift from the seed:
    //
    //   * RecurrenceMaxGeneratedOccurrences        tighten-only  — [Range] max IS this value
    //   * ContactVCardMaxRepeatablePropertiesPerEntry  tighten-only  — [Range] max IS this value
    //   * EmailMaxTrackedRecipients                raise-only    — [Range] min IS this value
    //
    // See the remarks on each for WHY the other direction is unavailable; the short version is that
    // two of them are write amplifiers whose cost survives lowering the setting back, and the third
    // is a security control that fails OPEN when its table fills.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Model output cap on the extraction and match calls. A direct third-party spend lever.</summary>
    public const int FileAnalysisMaxTokens = 8096;

    /// <summary>Per-list vocabulary cap; over it the LLM match is skipped, not truncated.</summary>
    public const int FileAnalysisMatchMaxVocabulary = 500;

    /// <summary>Hard per-match-call timeout; on timeout the job records MatchStatus = Failed.</summary>
    public const int FileAnalysisMatchTimeoutSeconds = 60;

    /// <summary>
    /// Blob prefix read for photo metadata extraction, in megabytes. <strong>16 is the ceiling</strong>
    /// and it is a compiled assumption about MariaDB's default <c>max_allowed_packet</c> (16 MiB),
    /// which this repository pins nowhere — see <c>docs/deployment.md</c>. Extraction reads a full
    /// <c>byte[]</c> of this size per photo, so it is also a per-upload memory multiplier.
    /// </summary>
    public const int PhotoMetadataReadMegabytes = 8;

    /// <summary>Wall-clock timeout for one metadata extraction, in seconds.</summary>
    public const int PhotoMetadataExtractionTimeoutSeconds = 5;

    /// <summary>Widest From/To window a calendar-event list query may span, in days.</summary>
    public const int CalendarMaxWindowDays = 92;

    /// <summary>Longest a single calendar event may span, in days.</summary>
    public const int CalendarMaxEventDurationDays = 366;

    /// <summary>Bounded fetch guard on the no-filter aggregate ICS export path.</summary>
    public const int CalendarIcsMaxAggregateExportRows = 20_000;

    /// <summary>Aggregate occurrence budget for one ICS import, held in memory while it runs.</summary>
    public const int CalendarIcsMaxAggregateOccurrences = 5000;

    /// <summary>Widest From/To window an aggregate ICS export may span, in days.</summary>
    public const int CalendarIcsMaxAggregateExportWindowDays = 92;

    /// <summary>
    /// Occurrences one recurrence pattern may generate. <strong>Tighten-only.</strong>
    /// <c>RecurrencePatternService</c> persists one <c>CalendarEvent</c> row per generated occurrence,
    /// so this is a write amplifier, not a materialisation bound: the cost of a raise survives
    /// lowering the setting back, the write is available to any principal holding
    /// <c>calendar.create</c> (Owner and User both do), and there is no rate limiter on that path.
    /// Lowering it is useful and completely safe, so only that direction is offered.
    /// </summary>
    public const int RecurrenceMaxGeneratedOccurrences = 1000;

    /// <summary>
    /// Repeatable vCard properties (ADR/EMAIL/TEL) accepted in one entry. <strong>Tighten-only.</strong>
    /// Each one costs a sibling <c>ToListAsync</c> <em>and</em> its own <c>SaveChangesAsync</c>, and
    /// that is multiplied by <c>ContactVCardMaxImportEntries</c>, which ships "unlimited" — so any
    /// numeric ceiling here would be a guess about a product of three unbounded terms.
    /// </summary>
    public const int ContactVCardMaxRepeatablePropertiesPerEntry = 200;

    /// <summary>Sample titles kept per skip reason in an import summary. Every skip is still counted.</summary>
    public const int ImportMaxSamplesPerSkipReason = 100;

    /// <summary>
    /// Addresses the per-recipient mail throttle tracks at once. <strong>Raise-only</strong>, floor
    /// pinned here. The throttle <em>fails open</em> at capacity, so a smaller table weakens the
    /// anti-mailbomb control rather than tightening it; ~2 MB at 20,000 entries makes removing the
    /// weaken-direction free. This is also the one setting in issue #434 whose degraded read resolves
    /// to <c>max</c> rather than <c>min</c>.
    /// </summary>
    public const int EmailMaxTrackedRecipients = 20_000;

    /// <summary>Smart tags one account may carry.</summary>
    public const int AccountMaxSmartTagsPerAccount = 20;

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // The file-analysis kill switch, model and destination (issue #439). These three were the last
    // FileAnalysis values an administrator would reasonably want to change without a redeploy; the
    // API key deliberately stays a deploy-time secret.
    //
    // All three mirror what appsettings.json shipped, so a default install is behaviourally
    // identical — analysis OFF, claude-sonnet-5, api.anthropic.com — and an operator's configured
    // value an administrator sets at /settings replaces these; configuration never does.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether AI document analysis is permitted at all. <strong>Fail closed:</strong> a failed or
    /// unusable settings read resolves to <c>false</c>, because this is the switch that stops personal
    /// data leaving the deployment for a third-party processor. Read LIVE on every call, never cached
    /// — see <c>IFileAnalysisSettingsLookup.IsEnabledAsync</c>.
    /// </summary>
    public const bool FileAnalysisEnabled = false;

    /// <summary>
    /// The model each analysis is sent to and stamped with. A degraded read resolves this to
    /// <c>null</c> and <em>refuses</em> the analysis rather than substituting this default: stamping
    /// <c>AnalyzerModel</c> with a model that did not run is the provenance corruption issue #421
    /// Non-Goal 6 was protecting against, and substitution is the only mechanism that could cause it.
    /// </summary>
    public const string FileAnalysisModel = "claude-sonnet-5";

    /// <summary>
    /// Where analysis requests are sent. Host only — the provider appends <c>/v1/messages</c> itself,
    /// and the write validator rejects any path for exactly that reason. Like the model, a degraded
    /// read resolves to <c>null</c> and refuses: falling back to this value would transfer a document
    /// to Anthropic when the administrator had deliberately pointed the deployment at a gateway.
    /// </summary>
    public const string FileAnalysisBaseUrl = "https://api.anthropic.com";

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // The Subscriptions summary limits (issue #437). The first two seed the `private const`s they
    // replace exactly, so a default install is behaviourally identical. The third is new: the
    // summary's fetch was UNBOUNDED, and 1000 matches its two shipped siblings
    // (InsuranceMaxSummaryPolicies, ContractMaxSummaryContracts).
    //
    // None of the three is single-direction — none is a write amplifier and none is a security
    // control that fails open — so no [Range] end is pinned at one of these constants.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How many days ahead a subscription's next billing date is surfaced as an upcoming renewal.
    /// A degraded read resolves to <c>min</c>, but for a correctness reason rather than a load one:
    /// the window drives no work at all (<c>BuildRenewals</c> iterates an already-fetched list), so
    /// the preference is to <strong>under-report</strong> renewals rather than over-report them.
    /// </summary>
    public const int SubscriptionRenewalWindowDays = 45;

    /// <summary>
    /// Renewal rows the page-header roll-up lists. Each is a separate rendered block above the list,
    /// which is why its bound is 50 rather than the 100000 its sibling caps carry.
    /// </summary>
    public const int SubscriptionMaxSummaryRenewals = 6;

    /// <summary>
    /// Subscriptions read to compute the roll-up. Conservative direction for a degraded read is
    /// <c>min</c> — it is a cap on a materialised fetch.
    /// </summary>
    public const int SubscriptionMaxSummarySubscriptions = 1000;
}
