namespace Odyssey.Dtos;

/// <summary>
/// The <em>bound pair</em> — minimum and maximum — of every <c>int</c>-valued admin-configurable
/// system setting (issue #437 §6). One pair per key, named by all four of its consumers: the
/// <c>[Range]</c> on <c>SystemSettingsUpdate</c>, the registry descriptor, the read-path clamp in the
/// domain lookups, and the client catalogue's <c>Min</c>/<c>Max</c>.
///
/// <para>
/// <strong>This is not <see cref="SystemSettingsDefaults"/>, and conflating the two is what produced
/// the first draft of issue #437's read-path contract.</strong> <c>SystemSettingsDefaults</c> holds
/// what a <em>missing</em> row falls back to; this class holds what a <em>present-but-out-of-range</em>
/// row is clamped to. A row that parses and sits outside its pair is clamped to the nearer bound and
/// reported — it is neither obeyed nor reverted to the default.
/// </para>
///
/// <para>
/// <strong>Why a pair rather than a ceiling with a hardcoded floor of 1.</strong> Three keys already
/// have a <c>[Range]</c> minimum above 1, and one of them — <see cref="EmailMaxTrackedRecipientsMin"/>
/// — is raise-only <em>because its floor is the load-bearing end</em>: the per-recipient mail throttle
/// fails open once its table is full, so a hand-edited <c>1</c> would weaken an anti-mailbomb control
/// rather than tighten it. A maximum-only guard would have accepted that.
/// </para>
///
/// <para>
/// <strong>Three pairs alias <see cref="SystemSettingsDefaults"/> rather than restating its
/// literal.</strong> Those ends are the pinned end of a single-direction key, and <c>CLAUDE.md</c> is
/// explicit that such an end is expressed "by naming the <c>SystemSettingsDefaults</c> constant —
/// never a second literal that could drift from the seed". Both classes live in this project, so the
/// alias is free.
/// </para>
///
/// <para>
/// It lives here, beside the defaults, for the same reason they do: an attribute argument must be a
/// compile-time constant naming a real symbol, <c>SystemSettingsUpdate</c> lives in
/// <c>Odyssey.Dtos.Application</c>, and this assembly is that project's only project reference. The
/// WebAssembly client can reach it too (client → <c>Application.Dtos</c> → here), unlike
/// <c>SystemSettingsKeys</c>.
/// </para>
/// </summary>
public static class SystemSettingsBounds
{
    // ── The two insurance knobs (issue #349) ─────────────────────────────────────────────────────

    public const int InsuranceExpiringSoonWindowDaysMin = 1;
    public const int InsuranceExpiringSoonWindowDaysMax = 365;

    public const int InsuranceMaxSummaryPoliciesMin = 1;
    public const int InsuranceMaxSummaryPoliciesMax = 100000;

    // ── The eight import/export size (MB) caps (issue #343 + follow-ups) ─────────────────────────

    public const int ContactVCardMaxImportMegabytesMin = 1;
    public const int ContactVCardMaxImportMegabytesMax = 1024;

    public const int ContactVCardMaxExportMegabytesMin = 1;
    public const int ContactVCardMaxExportMegabytesMax = 1024;

    public const int CalendarIcsMaxImportMegabytesMin = 1;
    public const int CalendarIcsMaxImportMegabytesMax = 1024;

    public const int CalendarIcsMaxExportMegabytesMin = 1;
    public const int CalendarIcsMaxExportMegabytesMax = 1024;

    public const int TaskIcsMaxImportMegabytesMin = 1;
    public const int TaskIcsMaxImportMegabytesMax = 1024;

    public const int TaskIcsMaxExportMegabytesMin = 1;
    public const int TaskIcsMaxExportMegabytesMax = 1024;

    public const int JournalIcsMaxImportMegabytesMin = 1;
    public const int JournalIcsMaxImportMegabytesMax = 1024;

    public const int JournalIcsMaxExportMegabytesMin = 1;
    public const int JournalIcsMaxExportMegabytesMax = 1024;

    // ── AI file-analysis policy (issue #421 Wave 1) ──────────────────────────────────────────────

    public const int FileAnalysisMaxFutureTransactionDaysMin = 1;
    public const int FileAnalysisMaxFutureTransactionDaysMax = 3650;

    // ── Transactional email (issue #421 Wave 2) ──────────────────────────────────────────────────

    /// <summary>
    /// The SMTP port's pair (issue #8) — the whole TCP port space, because a relay may legitimately
    /// sit anywhere. Narrowing it to the three conventional ports (25/465/587) was considered and
    /// rejected: an internal relay on a non-standard port is a normal deployment, and a bound that
    /// refuses one would be worked around by putting the value back into configuration, which is what
    /// this change exists to remove.
    ///
    /// <para>
    /// The read-path clamp against this pair is what a hand-edited or restored <c>0</c> resolves to.
    /// It is a clamp, not a fail-closed condition, because a port that parses is a usable number —
    /// unlike an unparseable one, which the send path refuses outright.
    /// </para>
    /// </summary>
    public const int EmailSmtpPortMin = 1;

    public const int EmailSmtpPortMax = 65535;

    public const int EmailPerRecipientLimitMin = 1;
    public const int EmailPerRecipientLimitMax = 1000;

    public const int EmailPerRecipientWindowMinutesMin = 1;
    public const int EmailPerRecipientWindowMinutesMax = 1440;

    // ── Per-request defensive caps (issue #421 Wave 3) ───────────────────────────────────────────

    public const int ContractMaxPartiesPerContractMin = 1;
    public const int ContractMaxPartiesPerContractMax = 100000;

    public const int ContractMaxFilesPerContractMin = 1;
    public const int ContractMaxFilesPerContractMax = 100000;

    public const int ContractMaxSummaryContractsMin = 1;
    public const int ContractMaxSummaryContractsMax = 100000;

    public const int InsuranceMaxRenewalsPerPolicyMin = 1;
    public const int InsuranceMaxRenewalsPerPolicyMax = 100000;

    public const int InsuranceMaxFilesPerParentMin = 1;
    public const int InsuranceMaxFilesPerParentMax = 100000;

    public const int InsuranceMaxLinksPerPolicyMin = 1;
    public const int InsuranceMaxLinksPerPolicyMax = 100000;

    public const int PhotoMaxLinksPerKindMin = 1;
    public const int PhotoMaxLinksPerKindMax = 100000;

    public const int PhotoMaxAlbumMembersMin = 1;
    public const int PhotoMaxAlbumMembersMax = 100000;

    public const int JournalEntryMaxLinksPerKindMin = 1;
    public const int JournalEntryMaxLinksPerKindMax = 100000;

    public const int JournalTaskMaxLinksPerKindMin = 1;
    public const int JournalTaskMaxLinksPerKindMax = 100000;

    // ── The upload cap (issue #421 Wave 4) ───────────────────────────────────────────────────────

    public const int FileStorageMaxUploadMegabytesMin = 1;
    public const int FileStorageMaxUploadMegabytesMax = 1024;

    // ── The last compiled-in tuning constants (issue #434) ───────────────────────────────────────

    public const int FileAnalysisMaxTokensMin = 1024;
    public const int FileAnalysisMaxTokensMax = 64000;

    public const int FileAnalysisMatchMaxVocabularyMin = 1;
    public const int FileAnalysisMatchMaxVocabularyMax = 5000;

    public const int FileAnalysisMatchTimeoutSecondsMin = 5;
    public const int FileAnalysisMatchTimeoutSecondsMax = 600;

    public const int PhotoMetadataReadMegabytesMin = 1;
    public const int PhotoMetadataReadMegabytesMax = 16;

    public const int PhotoMetadataExtractionTimeoutSecondsMin = 1;
    public const int PhotoMetadataExtractionTimeoutSecondsMax = 120;

    public const int CalendarMaxWindowDaysMin = 1;
    public const int CalendarMaxWindowDaysMax = 3650;

    public const int CalendarMaxEventDurationDaysMin = 1;
    public const int CalendarMaxEventDurationDaysMax = 3650;

    public const int CalendarIcsMaxAggregateExportRowsMin = 1;
    public const int CalendarIcsMaxAggregateExportRowsMax = 40000;

    public const int CalendarIcsMaxAggregateOccurrencesMin = 1;
    public const int CalendarIcsMaxAggregateOccurrencesMax = 20000;

    public const int CalendarIcsMaxAggregateExportWindowDaysMin = 1;
    public const int CalendarIcsMaxAggregateExportWindowDaysMax = 3650;

    public const int RecurrenceMaxGeneratedOccurrencesMin = 1;

    /// <summary>
    /// Tighten-only: the maximum <strong>is</strong> the shipped default, named rather than restated.
    /// </summary>
    public const int RecurrenceMaxGeneratedOccurrencesMax =
        SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences;

    public const int ContactVCardMaxRepeatablePropertiesPerEntryMin = 1;

    /// <summary>
    /// Tighten-only: the maximum <strong>is</strong> the shipped default, named rather than restated.
    /// </summary>
    public const int ContactVCardMaxRepeatablePropertiesPerEntryMax =
        SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry;

    public const int ImportMaxSamplesPerSkipReasonMin = 1;
    public const int ImportMaxSamplesPerSkipReasonMax = 10000;

    /// <summary>
    /// Raise-only, and <strong>this is the load-bearing end</strong>: the per-recipient mail throttle
    /// fails open once its table is full, so a smaller table weakens the control. The minimum
    /// <strong>is</strong> the shipped default, named rather than restated.
    /// </summary>
    public const int EmailMaxTrackedRecipientsMin = SystemSettingsDefaults.EmailMaxTrackedRecipients;

    public const int EmailMaxTrackedRecipientsMax = 200000;

    public const int AccountMaxSmartTagsPerAccountMin = 1;
    public const int AccountMaxSmartTagsPerAccountMax = 1000;

    // ── The Subscriptions summary limits (issue #437) ────────────────────────────────────────────
    //
    // Declared HERE first and named by the [Range] rather than transcribed from it — the sourcing
    // runs the other way for these three, unlike the 38 keys above.

    public const int SubscriptionRenewalWindowDaysMin = 1;

    /// <summary>Matches <see cref="InsuranceExpiringSoonWindowDaysMax"/> — the same shape of window.</summary>
    public const int SubscriptionRenewalWindowDaysMax = 365;

    public const int SubscriptionMaxSummaryRenewalsMin = 1;

    /// <summary>
    /// <strong>50, not 100000.</strong> Each renewal is rendered as its own block in the page-header
    /// roll-up, which is open by default and has no <c>max-height</c> or scroll container of its own
    /// (that component fix is issue #443). 50 is ~8x the shipped default — ample for a header roll-up
    /// — and tightening the global bound is what removes the need for a separate surface constant.
    /// </summary>
    public const int SubscriptionMaxSummaryRenewalsMax = 50;

    public const int SubscriptionMaxSummarySubscriptionsMin = 1;

    public const int SubscriptionMaxSummarySubscriptionsMax = 100000;
}
