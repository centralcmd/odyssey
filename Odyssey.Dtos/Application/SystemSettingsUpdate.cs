using System.ComponentModel.DataAnnotations;
using Odyssey.Dtos;

namespace Odyssey.Dtos.Application;

/// <summary>
/// The write shape for <c>PUT /api/system-settings</c> (issue #349). Every field is nullable, and
/// <see langword="null"/> means "leave this field unchanged" — not zero/false: no claim check, no
/// validation, no comparison against the current stored value, and that key's row is left completely
/// untouched (its <c>UpdatedAt</c>/<c>UpdatedBy</c> included). A non-null value means "set this field",
/// which requires the matching write claim (<c>system-settings.update</c> for the two Insurance fields,
/// <c>system-settings.security.update</c> for the three perimeter fields) and passes normal validation.
///
/// This is what makes per-field authorization safe under a whole-resource-shaped <c>PUT</c> without a
/// concurrency token: a caller who structurally cannot edit a field sends <see langword="null"/> for
/// it, not the value it loaded, so there is nothing to compare against another admin's concurrent
/// change to that same field. There are deliberately no id/audit fields here — those are always
/// server-assigned, never accepted from the request body.
/// </summary>
public sealed record SystemSettingsUpdate
{
    public bool? RequireTwoFactor { get; set; }

    public bool? RegistrationRequireAdminApproval { get; set; }

    public bool? EmailRequireConfirmation { get; set; }

    [Range(SystemSettingsBounds.InsuranceExpiringSoonWindowDaysMin,
        SystemSettingsBounds.InsuranceExpiringSoonWindowDaysMax, ErrorMessage =
        "The \"expiring soon\" window must be between 1 and 365 days. It is also the bound the read "
        + "path clamps a stored value into, so the two cannot disagree.")]
    public int? InsuranceExpiringSoonWindowDays { get; set; }

    [Range(SystemSettingsBounds.InsuranceMaxSummaryPoliciesMin,
        SystemSettingsBounds.InsuranceMaxSummaryPoliciesMax, ErrorMessage =
        "Policies read for the summary must be between 1 and 100000. Above the cap the roll-up covers "
        + "the most recent policies only.")]
    public int? InsuranceMaxSummaryPolicies { get; set; }

    // ---------------------------------------------------------------------------------------------
    // Import/export volume caps (issue #343 §6/§9, extended post-#343 with a "maximum export file
    // size" per surface plus a Tasks export row cap). Count fields use CapacityLimit's three-state
    // shape (null = leave unchanged; { unlimited: true } = no limit; { value: n } = n) rather than
    // int?, because on this DTO null already means "leave unchanged" for every field — reusing int?
    // for a count field would make "unlimited" inexpressible. Size fields stay int? (megabytes) and
    // have no unlimited option, on either side — all four surfaces share the same [Range(1, 1024)]
    // (issue #343 §5's original per-surface split — contacts up to 512 MB, ICS up to 1024 MB — has
    // since been unified per a follow-up: contacts allows just as much as the other three now).
    //
    // The claim check for every CapacityLimit field below MUST use SystemSettingsService's
    // RequireClaim(ClaimsPrincipal, CapacityLimit?, ...) overload, never `.Value` — see sec F3.
    // ---------------------------------------------------------------------------------------------

    public CapacityLimit? ContactVCardMaxExportRows { get; set; }

    public CapacityLimit? ContactVCardMaxImportEntries { get; set; }

    [Range(1, 1024)]
    public int? ContactVCardMaxImportMegabytes { get; set; }

    [Range(1, 1024)]
    public int? ContactVCardMaxExportMegabytes { get; set; }

    public CapacityLimit? CalendarIcsMaxExportEvents { get; set; }

    public CapacityLimit? CalendarIcsMaxImportEvents { get; set; }

    [Range(1, 1024)]
    public int? CalendarIcsMaxImportMegabytes { get; set; }

    [Range(1, 1024)]
    public int? CalendarIcsMaxExportMegabytes { get; set; }

    public CapacityLimit? TaskIcsMaxExportTasks { get; set; }

    public CapacityLimit? TaskIcsMaxImportTasks { get; set; }

    [Range(1, 1024)]
    public int? TaskIcsMaxImportMegabytes { get; set; }

    [Range(1, 1024)]
    public int? TaskIcsMaxExportMegabytes { get; set; }

    public CapacityLimit? JournalIcsMaxExportRows { get; set; }

    public CapacityLimit? JournalIcsMaxImportEntries { get; set; }

    [Range(1, 1024)]
    public int? JournalIcsMaxImportMegabytes { get; set; }

    [Range(1, 1024)]
    public int? JournalIcsMaxExportMegabytes { get; set; }

    // ---------------------------------------------------------------------------------------------
    // AI file-analysis policy and processor disclosure (issue #421 Wave 1). No [Required] on the four
    // strings: null already means "leave unchanged" on this DTO, so [Required] would reject the
    // legitimate unchanged-null. Empty is rejected in the descriptor's Validate instead, as a 400.
    // ---------------------------------------------------------------------------------------------

    [StringLength(128)]
    public string? FileAnalysisProcessor { get; set; }

    [StringLength(128)]
    public string? FileAnalysisProcessorRegion { get; set; }

    [StringLength(128)]
    public string? FileAnalysisLawfulBasis { get; set; }

    [StringLength(256)]
    public string? FileAnalysisPrivacyNoticeUrl { get; set; }

    [Range(1, 3650)]
    public int? FileAnalysisMaxFutureTransactionDays { get; set; }

    [Range(0.0, 1.0)]
    public decimal? FileAnalysisMatchAutoLinkThreshold { get; set; }

    // ---------------------------------------------------------------------------------------------
    // Transactional email (issue #421 Wave 2, extended by issue #8). Ranges carried over from the
    // retired EmailOptions annotations so a direct (non-HTTP) caller is bounded the same way model
    // validation bounds a request. No [Required] on the strings — null means "leave unchanged" on
    // this DTO.
    //
    // The four transport keys are the last Email:* values that were still deploy-time only. Two of
    // them accept the EMPTY STRING as a meaningful value ("mail is not configured", "links cannot be
    // composed") rather than as a rejected clear — see StringSetting.AllowEmpty. That is why neither
    // carries [Required] and why neither has a MinimumLength: null and "" mean different things here,
    // and an attribute cannot express the difference.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The relay. Changing this to a different non-empty value CLEARS the stored SMTP username and
    /// password in the same transaction (issue #8 G4) — the sender connects before it authenticates,
    /// so a credential entered for one relay must never be presented to another.
    /// </summary>
    [StringLength(EmailSmtpHostRule.MaxLength)]
    public string? EmailSmtpHost { get; set; }

    [Range(SystemSettingsBounds.EmailSmtpPortMin, SystemSettingsBounds.EmailSmtpPortMax)]
    public int? EmailSmtpPort { get; set; }

    /// <summary>
    /// Turning this OFF clears the same two secrets (issue #8 G7), for the same reason in a different
    /// shape: a credential entered for an encrypted transport must not be replayed over a cleartext
    /// one, where passive network position alone is enough to harvest it.
    /// </summary>
    public bool? EmailUseStartTls { get; set; }

    /// <summary>
    /// The public origin every confirmation and password-reset link is composed against. The
    /// highest-consequence field in this group and the one G4/G7 do NOT protect — no credential is
    /// involved, so there is nothing to clear. See issue #8 §10.2.
    /// </summary>
    [StringLength(EmailClientBaseUrlRule.MaxLength)]
    public string? EmailClientBaseUrl { get; set; }

    [StringLength(256)]
    public string? EmailFromAddress { get; set; }

    [StringLength(128)]
    public string? EmailFromName { get; set; }

    [Range(1, 1000)]
    public int? EmailPerRecipientLimit { get; set; }

    [Range(1, 1440)]
    public int? EmailPerRecipientWindowMinutes { get; set; }

    // ---------------------------------------------------------------------------------------------
    // Per-request defensive caps (issue #421 Wave 3). The two photo caps carry the same wide range as
    // the rest; their real bound is the tighten-only ceiling enforced server-side, because a
    // [MaxLength] constant cannot be expressed in an attribute here.
    // ---------------------------------------------------------------------------------------------

    [Range(SystemSettingsBounds.ContractMaxPartiesPerContractMin,
        SystemSettingsBounds.ContractMaxPartiesPerContractMax, ErrorMessage =
        "Parties per contract must be between 1 and 100000.")]
    public int? ContractMaxPartiesPerContract { get; set; }

    [Range(SystemSettingsBounds.ContractMaxFilesPerContractMin,
        SystemSettingsBounds.ContractMaxFilesPerContractMax, ErrorMessage =
        "Files per contract must be between 1 and 100000.")]
    public int? ContractMaxFilesPerContract { get; set; }

    [Range(SystemSettingsBounds.ContractMaxSummaryContractsMin,
        SystemSettingsBounds.ContractMaxSummaryContractsMax, ErrorMessage =
        "Contracts read for the summary must be between 1 and 100000. Above the cap the roll-up "
        + "covers the most recent contracts only.")]
    public int? ContractMaxSummaryContracts { get; set; }

    [Range(SystemSettingsBounds.InsuranceMaxRenewalsPerPolicyMin,
        SystemSettingsBounds.InsuranceMaxRenewalsPerPolicyMax, ErrorMessage =
        "Renewals per policy must be between 1 and 100000.")]
    public int? InsuranceMaxRenewalsPerPolicy { get; set; }

    [Range(SystemSettingsBounds.InsuranceMaxFilesPerParentMin,
        SystemSettingsBounds.InsuranceMaxFilesPerParentMax, ErrorMessage =
        "Files per policy or renewal must be between 1 and 100000.")]
    public int? InsuranceMaxFilesPerParent { get; set; }

    [Range(1, 100000)]
    public int? PhotoMaxLinksPerKind { get; set; }

    [Range(1, 100000)]
    public int? PhotoMaxAlbumMembers { get; set; }

    [Range(1, 100000)]
    public int? JournalEntryMaxLinksPerKind { get; set; }

    [Range(1, 100000)]
    public int? JournalTaskMaxLinksPerKind { get; set; }

    /// <summary>
    /// The upload cap, in megabytes (issue #421 Wave 4). The advertised range is not the effective
    /// range — the value must also be at or below the startup transport ceiling, which the server
    /// validates and publishes as <c>UploadMegabytesCeiling</c> on the read DTO.
    /// </summary>
    [Range(1, 1024)]
    public int? FileStorageMaxUploadMegabytes { get; set; }

    // ---------------------------------------------------------------------------------------------
    // The last compiled-in tuning constants (issue #434).
    //
    // [Range] IS the bound for all six bounded keys here — there is no second RequestCapCeilings
    // validator, deliberately. Model validation runs FIRST, so a validator whose limit equalled the
    // range limit could never fire; that layer was deleted rather than left decorative (§9). The
    // photo/upload ceilings keep theirs because those are runtime- or cross-assembly-derived and
    // genuinely cannot live in an attribute.
    //
    // Three keys are SINGLE-DIRECTION, and their pinned end names the shared constant rather than
    // restating a literal that could drift from the seed:
    //
    //   * RecurrenceMaxGeneratedOccurrences            max = default → tighten-only
    //   * ContactVCardMaxRepeatablePropertiesPerEntry   max = default → tighten-only
    //   * EmailMaxTrackedRecipients                     min = default → raise-only
    //
    // Widening one of those ranges "so a ceiling has something to reject" is the tempting fix that
    // would re-open the write amplification the tighten-only conversion closed. A guard test asserts
    // the pinned end equals the shared default, which makes that change fail the build.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Model output cap on the file-analysis calls. A direct third-party spend lever, which is why it
    /// carries the security claim and is therefore audited.
    /// </summary>
    [Range(1024, 64000, ErrorMessage =
        "Max tokens must be between 1024 and 64000. It caps the model's output on every analysis, so "
        + "it bounds both extraction completeness and per-request API spend.")]
    public int? FileAnalysisMaxTokens { get; set; }

    [Range(1, 5000, ErrorMessage =
        "Match vocabulary cap must be between 1 and 5000. Over the cap the match step is skipped "
        + "rather than truncated, and every name in it is sent to the provider on each match call.")]
    public int? FileAnalysisMatchMaxVocabulary { get; set; }

    [Range(5, 600, ErrorMessage =
        "Match timeout must be between 5 and 600 seconds. On timeout the job records a failed match "
        + "and falls back to manual review.")]
    public int? FileAnalysisMatchTimeoutSeconds { get; set; }

    /// <summary>
    /// Blob prefix read for photo metadata extraction, in megabytes. The 16 MB maximum is a compiled
    /// assumption about MariaDB's default <c>max_allowed_packet</c>, which this repository pins
    /// nowhere — see <c>docs/deployment.md</c>.
    /// </summary>
    [Range(1, 16, ErrorMessage =
        "Metadata read size must be between 1 and 16 MB. Extraction materialises a full byte array of "
        + "this size per photo, and 16 MB is MariaDB's default max_allowed_packet — the real wall on "
        + "the prefix read, whatever a larger value here claimed.")]
    public int? PhotoMetadataReadMegabytes { get; set; }

    [Range(1, 120, ErrorMessage =
        "Metadata extraction timeout must be between 1 and 120 seconds. On timeout the photo is still "
        + "stored, just without extracted metadata.")]
    public int? PhotoMetadataExtractionTimeoutSeconds { get; set; }

    [Range(1, 3650, ErrorMessage =
        "The calendar window must be between 1 and 3650 days. It bounds how much of the calendar one "
        + "list request may span.")]
    public int? CalendarMaxWindowDays { get; set; }

    [Range(1, 3650, ErrorMessage = "A single event may span between 1 and 3650 days.")]
    public int? CalendarMaxEventDurationDays { get; set; }

    /// <summary>
    /// Bounded fetch guard on the aggregate ICS export path. The 40,000 maximum is 2x the shipped
    /// default — derived from the concurrency actually permitted on that surface (2 import permits
    /// globally, 2 exports per user, 4 exports globally), so worst-case concurrent materialisation
    /// stays within the same order as today's.
    /// </summary>
    [Range(1, 40000, ErrorMessage =
        "The aggregate export row guard must be between 1 and 40000. Its maximum is twice the shipped "
        + "default, so worst-case concurrent materialisation stays within the same order as today's.")]
    public int? CalendarIcsMaxAggregateExportRows { get; set; }

    /// <summary>Aggregate occurrence budget for one ICS import. Maximum is 2x the shipped default, same derivation.</summary>
    [Range(1, 20000, ErrorMessage =
        "The aggregate occurrence budget must be between 1 and 20000. Every occurrence is materialised "
        + "in memory during an import, and its maximum is twice the shipped default.")]
    public int? CalendarIcsMaxAggregateOccurrences { get; set; }

    [Range(1, 3650, ErrorMessage =
        "The aggregate export window must be between 1 and 3650 days.")]
    public int? CalendarIcsMaxAggregateExportWindowDays { get; set; }

    /// <summary>
    /// <strong>Tighten-only.</strong> The maximum IS the shipped default, expressed by reference so it
    /// cannot drift from the seed.
    /// </summary>
    [Range(1, SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences, ErrorMessage =
        "Generated occurrences can only be lowered, not raised. Each occurrence is persisted as its own "
        + "calendar row, so raising this hands every user a write multiplier whose cost survives "
        + "lowering the setting back.")]
    public int? RecurrenceMaxGeneratedOccurrences { get; set; }

    /// <summary>
    /// <strong>Tighten-only.</strong> The maximum IS the shipped default, expressed by reference.
    /// </summary>
    [Range(1, SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry, ErrorMessage =
        "Repeatable vCard properties per entry can only be lowered, not raised. Each one costs a "
        + "sibling query and its own save, multiplied by an import entry cap that ships unlimited.")]
    public int? ContactVCardMaxRepeatablePropertiesPerEntry { get; set; }

    [Range(1, 10000, ErrorMessage =
        "Samples per skip reason must be between 1 and 10000. Every skip is counted regardless; this "
        + "only bounds how many example titles the import summary carries back.")]
    public int? ImportMaxSamplesPerSkipReason { get; set; }

    /// <summary>
    /// <strong>Raise-only.</strong> The minimum IS the shipped default, expressed by reference: the
    /// throttle fails <em>open</em> once its table is full, so a smaller table weakens the
    /// anti-mailbomb control rather than tightening it.
    /// </summary>
    [Range(SystemSettingsDefaults.EmailMaxTrackedRecipients, 200000, ErrorMessage =
        "Tracked recipients can only be raised, not lowered. The per-recipient throttle fails open once "
        + "its table is full, so a smaller table weakens the control it exists to provide.")]
    public int? EmailMaxTrackedRecipients { get; set; }

    [Range(1, 1000, ErrorMessage = "Smart tags per account must be between 1 and 1000.")]
    public int? AccountMaxSmartTagsPerAccount { get; set; }

    // ---------------------------------------------------------------------------------------------
    // The file-analysis kill switch, model and destination (issue #439). All three require
    // system-settings.security.update and are therefore audited by the derived AuditChanges rule.
    //
    // No [Required] on the two strings, for the reason stated at the top of this file: null already
    // means "leave unchanged" here, so [Required] would reject the legitimate unchanged-null. Empty is
    // rejected in StringSetting.Validate as a 400.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The runtime kill switch for third-party document transfer. Null leaves it unchanged; a
    /// non-null value requires <c>system-settings.security.update</c>.
    /// </summary>
    public bool? FileAnalysisEnabled { get; set; }

    /// <summary>
    /// The model each analysis runs on and is stamped with.
    ///
    /// <para>
    /// The 128 is a <strong>static</strong> bound and therefore belongs in this attribute rather than
    /// in a <c>RequestCapCeilings</c> validator. <c>FileAnalysisJob.AnalyzerModel</c> is
    /// <c>[StringLength(256)]</c>, so 128 is strictly tighter and the entity bound can never fire — a
    /// ceiling validator against 256 would be exactly the decorative ceiling <c>CLAUDE.md</c> warns
    /// about. A guard test asserts <c>128 &lt;= 256</c> against the entity's own attribute so the
    /// relationship cannot silently invert.
    /// </para>
    /// </summary>
    [StringLength(128, ErrorMessage =
        "The model name cannot exceed 128 characters. It is stamped verbatim on every analysis job.")]
    public string? FileAnalysisModel { get; set; }

    /// <summary>
    /// Where analysis requests are sent. Shape-validated by <c>FileAnalysisBaseUrlRule.Validate</c>:
    /// absolute <c>https</c>, no <c>userinfo</c>, no query, no fragment, and <strong>no path</strong>
    /// — the provider resolves a root-absolute <c>/v1/messages</c> against this, which would silently
    /// discard any path an administrator configured.
    /// </summary>
    [StringLength(256, ErrorMessage =
        "The provider base URL cannot exceed 256 characters.")]
    public string? FileAnalysisBaseUrl { get; set; }

    // ---------------------------------------------------------------------------------------------
    // The Subscriptions summary limits (issue #437). Each [Range] names its SystemSettingsBounds pair
    // rather than restating a literal: the SAME pair is the descriptor's bound, the read-path clamp at
    // both read sites, and the client catalogue's Min/Max, and AC 22 asserts all four agree.
    //
    // None of the three is single-direction — none is a write amplifier and none is a security control
    // that fails open — so no end is pinned at a SystemSettingsDefaults constant, and there is no
    // RequestCapCeilings entry: with no compile-time attribute ceiling and no startup limit upstream,
    // a ceiling validator would be decorative (§9).
    // ---------------------------------------------------------------------------------------------

    [Range(SystemSettingsBounds.SubscriptionRenewalWindowDaysMin,
        SystemSettingsBounds.SubscriptionRenewalWindowDaysMax, ErrorMessage =
        "The upcoming-renewals window must be between 1 and 365 days. A window of 0 would still "
        + "include same-day renewals, so it is rejected as out of range rather than as empty.")]
    public int? SubscriptionRenewalWindowDays { get; set; }

    /// <summary>
    /// Renewal rows the page-header roll-up lists. Bounded at 50 rather than the 100000 its sibling
    /// caps carry: each renewal is rendered as its own block above the list, in a region that is open
    /// by default and has no scroll container of its own.
    /// </summary>
    [Range(SystemSettingsBounds.SubscriptionMaxSummaryRenewalsMin,
        SystemSettingsBounds.SubscriptionMaxSummaryRenewalsMax, ErrorMessage =
        "Renewals shown in the summary must be between 1 and 50. Each one is a separate rendered "
        + "block in the page header, so this is deliberately bounded well below the other summary caps.")]
    public int? SubscriptionMaxSummaryRenewals { get; set; }

    [Range(SystemSettingsBounds.SubscriptionMaxSummarySubscriptionsMin,
        SystemSettingsBounds.SubscriptionMaxSummarySubscriptionsMax, ErrorMessage =
        "Subscriptions read for the summary must be between 1 and 100000. Above the cap the counts, "
        + "the run-rate and the upcoming-renewals list all cover the most recent subscriptions only.")]
    public int? SubscriptionMaxSummarySubscriptions { get; set; }
}
