using System.Text.Json.Serialization;

namespace Odyssey.Dtos.Application;

/// <summary>
/// The full, assembled read shape for <c>GET /api/system-settings</c> (issue #349). Built from the
/// five key-value <c>SystemSetting</c> rows; <see cref="UpdatedAt"/>/<see cref="UpdatedBy"/> reflect
/// the single most recent change across ALL keys (a deliberate v1 simplification — each row tracks
/// its own last writer independently, but this DTO surfaces only one summary line).
/// </summary>
public sealed record SystemSettingsDto
{
    /// <summary>Persisted, but not enforced anywhere — see <see cref="TwoFactorEnforced"/>.</summary>
    public bool RequireTwoFactor { get; set; }

    /// <summary>
    /// Always <see langword="false"/> today. A machine-readable sibling to <see cref="RequireTwoFactor"/>
    /// so no integration or compliance export can misread the stored toggle as an active control —
    /// there is no org-wide 2FA enforcement in this feature.
    /// </summary>
    public bool TwoFactorEnforced { get; set; }

    public bool RegistrationRequireAdminApproval { get; set; }

    public bool EmailRequireConfirmation { get; set; }

    public int InsuranceExpiringSoonWindowDays { get; set; }

    public int InsuranceMaxSummaryPolicies { get; set; }

    // ---------------------------------------------------------------------------------------------
    // The Subscriptions summary limits (issue #437). Instance policy, not records — three integers,
    // no PII, nothing reachable today only under another claim.
    // ---------------------------------------------------------------------------------------------

    public int SubscriptionRenewalWindowDays { get; set; }

    public int SubscriptionMaxSummaryRenewals { get; set; }

    public int SubscriptionMaxSummarySubscriptions { get; set; }

    // ---------------------------------------------------------------------------------------------
    // Import/export volume caps (issue #343 §6). Count fields are null = no limit; size fields are
    // always a finite number of megabytes.
    // ---------------------------------------------------------------------------------------------

    public int? ContactVCardMaxExportRows { get; set; }

    public int? ContactVCardMaxImportEntries { get; set; }

    public int ContactVCardMaxImportMegabytes { get; set; }

    public int ContactVCardMaxExportMegabytes { get; set; }

    public int? CalendarIcsMaxExportEvents { get; set; }

    public int? CalendarIcsMaxImportEvents { get; set; }

    public int CalendarIcsMaxImportMegabytes { get; set; }

    public int CalendarIcsMaxExportMegabytes { get; set; }

    public int? TaskIcsMaxExportTasks { get; set; }

    public int? TaskIcsMaxImportTasks { get; set; }

    public int TaskIcsMaxImportMegabytes { get; set; }

    public int TaskIcsMaxExportMegabytes { get; set; }

    public int? JournalIcsMaxExportRows { get; set; }

    public int? JournalIcsMaxImportEntries { get; set; }

    public int JournalIcsMaxImportMegabytes { get; set; }

    public int JournalIcsMaxExportMegabytes { get; set; }

    // ---------------------------------------------------------------------------------------------
    // AI file-analysis policy and processor disclosure (issue #421 Wave 1).
    // ---------------------------------------------------------------------------------------------

    /// <summary>Name of the third party the document is transferred to. GDPR Art. 13(1)(e).</summary>
    public string FileAnalysisProcessor { get; set; } = string.Empty;

    /// <summary>
    /// Where that processing happens. The field with the highest legal consequence here — it is what
    /// decides adequacy versus SCCs under GDPR Chapter V — and the one with no ground truth available
    /// to validate against, so a wrong value is an accepted admin-trust residual (issue #421 §3).
    /// </summary>
    public string FileAnalysisProcessorRegion { get; set; } = string.Empty;

    public string FileAnalysisLawfulBasis { get; set; } = string.Empty;

    /// <summary>Absolute https URL only — it is rendered into an href, so see the validator's remarks.</summary>
    public string FileAnalysisPrivacyNoticeUrl { get; set; } = string.Empty;

    public int FileAnalysisMaxFutureTransactionDays { get; set; }

    /// <summary>Confidence at or above which a match is auto-linked. Higher auto-links less.</summary>
    public decimal FileAnalysisMatchAutoLinkThreshold { get; set; }

    // ---------------------------------------------------------------------------------------------
    // Transactional-email sender identity and per-recipient throttle (issue #421 Wave 2). The SMTP
    // transport fields are deliberately absent — see Non-Goal 2.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Envelope sender. Must stay an address the relay is authorised to send as (SPF/DKIM).</summary>
    public string EmailFromAddress { get; set; } = string.Empty;

    public string EmailFromName { get; set; } = string.Empty;

    public int EmailPerRecipientLimit { get; set; }

    public int EmailPerRecipientWindowMinutes { get; set; }

    // ---------------------------------------------------------------------------------------------
    // Per-request defensive caps (issue #421 Wave 3).
    // ---------------------------------------------------------------------------------------------

    public int ContractMaxPartiesPerContract { get; set; }

    public int ContractMaxFilesPerContract { get; set; }

    public int ContractMaxSummaryContracts { get; set; }

    public int InsuranceMaxRenewalsPerPolicy { get; set; }

    public int InsuranceMaxFilesPerParent { get; set; }

    public int PhotoMaxLinksPerKind { get; set; }

    public int PhotoMaxAlbumMembers { get; set; }

    public int JournalEntryMaxLinksPerKind { get; set; }

    public int JournalTaskMaxLinksPerKind { get; set; }

    /// <summary>The effective upload cap, in megabytes (issue #421 Wave 4).</summary>
    public int FileStorageMaxUploadMegabytes { get; set; }

    /// <summary>
    /// Server-computed hard ceiling on <see cref="FileStorageMaxUploadMegabytes"/>: the startup
    /// transport ceiling, in megabytes.
    ///
    /// <para>
    /// Kestrel's request-body limit and the multipart length limit are fixed at startup from
    /// <c>FileStorage:MaxFileSizeBytes</c>, and neither can be raised per-request afterwards. A
    /// setting above that ceiling would therefore be rejected by the transport before any application
    /// code saw it — the upload would fail at a number the administrator never chose. So the cap is
    /// tighten-only in exactly the sense the two photo caps are, and the advertised
    /// <c>Range(1, 1024)</c> is deliberately not the effective range: this is.
    /// </para>
    /// </summary>
    public int UploadMegabytesCeiling { get; set; }

    /// <summary>
    /// Server-computed hard ceiling on <see cref="PhotoMaxLinksPerKind"/>.
    ///
    /// <para>
    /// The cap is tighten-only: its compile-time value feeds <c>[MaxLength]</c> on ten photo request
    /// DTOs, and <c>[ApiController]</c> model validation rejects an over-cap request before the service
    /// check is reached — so raising the setting above this would change nothing, which is exactly the
    /// "I raised the limit and it did not take effect" failure this feature refuses to ship. Surfaced
    /// so the control can bound itself rather than offering a value the API will reject.
    /// </para>
    /// </summary>
    public int PhotoMaxLinksPerKindCeiling { get; set; }

    /// <summary>Hard ceiling on <see cref="PhotoMaxAlbumMembers"/> — same reason.</summary>
    public int PhotoMaxAlbumMembersCeiling { get; set; }

    // ---------------------------------------------------------------------------------------------
    // The last compiled-in tuning constants (issue #434).
    // ---------------------------------------------------------------------------------------------

    /// <summary>Model output cap on the file-analysis calls — a direct third-party spend lever.</summary>
    public int FileAnalysisMaxTokens { get; set; }

    public int FileAnalysisMatchMaxVocabulary { get; set; }

    public int FileAnalysisMatchTimeoutSeconds { get; set; }

    /// <summary>Blob prefix read for photo metadata extraction, in megabytes.</summary>
    public int PhotoMetadataReadMegabytes { get; set; }

    public int PhotoMetadataExtractionTimeoutSeconds { get; set; }

    public int CalendarMaxWindowDays { get; set; }

    public int CalendarMaxEventDurationDays { get; set; }

    public int CalendarIcsMaxAggregateExportRows { get; set; }

    public int CalendarIcsMaxAggregateOccurrences { get; set; }

    public int CalendarIcsMaxAggregateExportWindowDays { get; set; }

    /// <summary>Tighten-only — see <see cref="RecurrenceMaxGeneratedOccurrencesCeiling"/>.</summary>
    public int RecurrenceMaxGeneratedOccurrences { get; set; }

    /// <summary>Tighten-only — see <see cref="ContactVCardMaxRepeatablePropertiesPerEntryCeiling"/>.</summary>
    public int ContactVCardMaxRepeatablePropertiesPerEntry { get; set; }

    public int ImportMaxSamplesPerSkipReason { get; set; }

    /// <summary>Raise-only — see <see cref="EmailMaxTrackedRecipientsFloor"/>.</summary>
    public int EmailMaxTrackedRecipients { get; set; }

    public int AccountMaxSmartTagsPerAccount { get; set; }

    // ---------------------------------------------------------------------------------------------
    // The file-analysis kill switch, model and destination (issue #439).
    //
    // Note what is NOT here and where it is not: the base URL is on this admin-gated DTO only. It is
    // deliberately absent from the claim-free FileAnalysisDisclosureDto — issue #421 justified that
    // widening field by field on the grounds that each value is disclosed to the user anyway, and this
    // one is not: it is deployment infrastructure and can name an internal host.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Whether AI document analysis is permitted at all. Read live, never from a cache.</summary>
    public bool FileAnalysisEnabled { get; set; }

    /// <summary>The model each analysis runs on and is stamped with. Completed jobs keep theirs.</summary>
    public string FileAnalysisModel { get; set; } = string.Empty;

    /// <summary>
    /// Where analysis requests are sent — host only, absolute https. The configured API key travels
    /// to whatever host is set here, which the row's advisory states in the UI.
    /// </summary>
    public string FileAnalysisBaseUrl { get; set; } = string.Empty;

    // ── The six bound projections (issue #434 §6) ────────────────────────────────────────────────
    //
    // These stay even though the server-side ceiling VALIDATORS for these keys were deleted (§9 —
    // [Range] is the bound, and a validator repeating the same number could never fire). The control
    // still has to bound itself, and for a WebAssembly client the read DTO is the only channel that
    // can carry the number. Five ceilings and one floor; the floor is the first on this DTO, and the
    // reason SettingItem needed a MinFrom to match its MaxFrom.

    /// <summary>Upper bound offered for <see cref="CalendarIcsMaxAggregateExportRows"/>.</summary>
    public int CalendarIcsMaxAggregateExportRowsCeiling { get; set; }

    /// <summary>Upper bound offered for <see cref="CalendarIcsMaxAggregateOccurrences"/>.</summary>
    public int CalendarIcsMaxAggregateOccurrencesCeiling { get; set; }

    /// <summary>Upper bound offered for <see cref="PhotoMetadataReadMegabytes"/>.</summary>
    public int PhotoMetadataReadMegabytesCeiling { get; set; }

    /// <summary>
    /// Upper bound offered for <see cref="RecurrenceMaxGeneratedOccurrences"/> — the shipped default,
    /// because the setting is tighten-only.
    /// </summary>
    public int RecurrenceMaxGeneratedOccurrencesCeiling { get; set; }

    /// <summary>
    /// Upper bound offered for <see cref="ContactVCardMaxRepeatablePropertiesPerEntry"/> — the shipped
    /// default, because the setting is tighten-only.
    /// </summary>
    public int ContactVCardMaxRepeatablePropertiesPerEntryCeiling { get; set; }

    /// <summary>
    /// Lower bound offered for <see cref="EmailMaxTrackedRecipients"/> — the shipped default, because
    /// the setting is raise-only.
    /// </summary>
    public int EmailMaxTrackedRecipientsFloor { get; set; }

    /// <summary>
    /// Non-blocking, per-field advisories from the server (issue #434 §5 item 3, closing #421's
    /// deferred D1). Keyed by the <see cref="SystemSettingsUpdate"/> property name — the <em>same</em>
    /// join key <c>ApiProblem.Errors</c> uses, so the client's existing field→row lookup works
    /// unchanged.
    ///
    /// <para>
    /// An advisory is not an error. It never changes the status code, never blocks a write, and never
    /// sets <c>aria-invalid</c> on its row: it says a saved value carries a cost worth knowing about
    /// (memory, CPU, third-party spend) or that two related settings look inconsistent. That is
    /// exactly why it needs a channel separate from <c>errors</c>.
    /// </para>
    ///
    /// <para>
    /// Two wire details are pinned rather than left to chance. The comparer is
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>, mirroring <c>ApiProblem.Errors</c>. And the
    /// serialized casing is stated explicitly with <see cref="JsonPropertyNameAttribute"/> on this
    /// property plus PascalCase keys inside it — PascalCase dictionary keys currently survive only
    /// because <c>DictionaryKeyPolicy</c> happens to be null, and a future global serializer change
    /// would otherwise silently break every advisory→row join at runtime rather than in a test.
    /// </para>
    /// </summary>
    [JsonPropertyName("warnings")]
    public IReadOnlyDictionary<string, string> Warnings { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which fields are <em>not</em> being read from their stored value, and why (issue #437 §5
    /// component 7). Names only — the human sentence stays in <see cref="Warnings"/>, keyed the same
    /// way, so the one-string-per-field rule that channel carries is untouched.
    ///
    /// <para>
    /// <strong>It exists because the kind is otherwise consumed on the server and never reaches the
    /// wire.</strong> §11's precedence rule resolves the advisory collision inside <c>Warnings</c>,
    /// which is one untyped string per field; without this map the client can build only a generic
    /// count — the very defect the projection advisory exists to remove — or string-match prose that
    /// no test locks and that breaks on the first copy edit.
    /// </para>
    ///
    /// <para>
    /// Same wire rules as <see cref="Warnings"/>: PascalCase keys matching the
    /// <c>SystemSettingsUpdate</c> property name, and an explicit
    /// <see cref="JsonPropertyNameAttribute"/>. The <see cref="StringComparer.OrdinalIgnoreCase"/> in
    /// the initializer is a <em>server-side construction detail</em>, not a wire rule — System.Text.Json
    /// constructs a fresh dictionary with the default ordinal comparer for an
    /// <see cref="IReadOnlyDictionary{TKey,TValue}"/> property and assigns it, discarding the
    /// initializer's. Harmless, because the client's join is exact-match on the same
    /// <c>nameof(SystemSettingsUpdate.X)</c> string; stated so a test does not build its inputs with a
    /// comparer the runtime never uses.
    /// </para>
    ///
    /// <para>
    /// There is no <c>JsonStringEnumConverter</c> in this solution, so <see cref="SettingFaultKind"/>
    /// crosses as its ordinal. That is the house default and both ends share this assembly.
    /// </para>
    /// </summary>
    [JsonPropertyName("projectionFaults")]
    public IReadOnlyDictionary<string, SettingFaultKind> ProjectionFaults { get; set; } =
        new Dictionary<string, SettingFaultKind>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The most recent write across all key-value rows.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>The id of the user who made that most-recent write. Null if no key has ever been written.</summary>
    public string? UpdatedBy { get; set; }

    /// <summary>Resolved server-side from <see cref="UpdatedBy"/> via the claim-aware display-name resolver.</summary>
    public string? UpdatedByDisplayName { get; set; }
}
