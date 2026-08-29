using System.Globalization;
using Odyssey.Dtos;

namespace Odyssey.Context;

/// <summary>
/// The known <see cref="SystemSetting.Key"/> values (issue #349) and the defaults they carry when a
/// row is ever missing/unreadable (should not happen post-migration — see the migration seed). Kept
/// here, alongside the entity, so both the migration's seed data and every reader (the live perimeter
/// reads in <see cref="OdysseyContext"/>, <c>Odyssey.Api.SystemSettings.SystemSettingsService</c>, and
/// the cached <c>ISystemSettingsLookup</c>/<c>IImportExportLimitsLookup</c>) share one set of literal
/// keys — no magic strings scattered across the codebase.
/// </summary>
public static class SystemSettingsKeys
{
    /// <summary>Persisted, but not enforced anywhere — see the feature's Non-Goals (issue #349).</summary>
    public const string RequireTwoFactor = "RequireTwoFactor";

    /// <summary>Perimeter field — always read live, never cached. See <see cref="OdysseyContext"/>.</summary>
    public const string RegistrationRequireAdminApproval = "RegistrationRequireAdminApproval";

    /// <summary>Perimeter field — always read live, never cached. See the <c>IUserConfirmation</c> seam.</summary>
    public const string EmailRequireConfirmation = "EmailRequireConfirmation";

    /// <summary>Cosmetic/policy field — cached with a 30s TTL by <c>ISystemSettingsLookup</c>.</summary>
    public const string InsuranceExpiringSoonWindowDays = "InsuranceExpiringSoonWindowDays";

    /// <summary>Cosmetic/policy field — cached with a 30s TTL by <c>ISystemSettingsLookup</c>.</summary>
    public const string InsuranceMaxSummaryPolicies = "InsuranceMaxSummaryPolicies";

    // ---------------------------------------------------------------------------------------------
    // Import/export volume caps (issue #343). Count fields serialize as either an invariant-culture
    // decimal integer or the literal "unlimited" sentinel (ParseCount/FormatCount below); size fields
    // are always a finite invariant-culture decimal integer, in megabytes.
    // ---------------------------------------------------------------------------------------------

    public const string ContactVCardMaxExportRows = "ContactVCardMaxExportRows";
    public const string ContactVCardMaxImportEntries = "ContactVCardMaxImportEntries";
    public const string ContactVCardMaxImportMegabytes = "ContactVCardMaxImportMegabytes";
    public const string ContactVCardMaxExportMegabytes = "ContactVCardMaxExportMegabytes";
    public const string CalendarIcsMaxExportEvents = "CalendarIcsMaxExportEvents";
    public const string CalendarIcsMaxImportEvents = "CalendarIcsMaxImportEvents";
    public const string CalendarIcsMaxImportMegabytes = "CalendarIcsMaxImportMegabytes";
    public const string CalendarIcsMaxExportMegabytes = "CalendarIcsMaxExportMegabytes";
    public const string TaskIcsMaxExportTasks = "TaskIcsMaxExportTasks";
    public const string TaskIcsMaxImportTasks = "TaskIcsMaxImportTasks";
    public const string TaskIcsMaxImportMegabytes = "TaskIcsMaxImportMegabytes";
    public const string TaskIcsMaxExportMegabytes = "TaskIcsMaxExportMegabytes";
    public const string JournalIcsMaxExportRows = "JournalIcsMaxExportRows";
    public const string JournalIcsMaxImportEntries = "JournalIcsMaxImportEntries";
    public const string JournalIcsMaxImportMegabytes = "JournalIcsMaxImportMegabytes";
    public const string JournalIcsMaxExportMegabytes = "JournalIcsMaxExportMegabytes";

    // ---------------------------------------------------------------------------------------------
    // AI file-analysis policy and processor disclosure (issue #421 Wave 1). The four disclosure
    // strings are what the consent gate shows the user at the point of consent (GDPR Art. 13); before
    // this they existed twice — here on the server, and again as client consts — and had already
    // drifted. Model, Provider, BaseUrl, ApiKey, TimeoutSeconds, PromptVersion and PromptTemplatePath
    // deliberately stay in deploy-time config (issue #421 Non-Goal 6).
    // ---------------------------------------------------------------------------------------------

    public const string FileAnalysisProcessor = "FileAnalysisProcessor";
    public const string FileAnalysisProcessorRegion = "FileAnalysisProcessorRegion";
    public const string FileAnalysisLawfulBasis = "FileAnalysisLawfulBasis";
    public const string FileAnalysisPrivacyNoticeUrl = "FileAnalysisPrivacyNoticeUrl";
    public const string FileAnalysisMaxFutureTransactionDays = "FileAnalysisMaxFutureTransactionDays";
    public const string FileAnalysisMatchAutoLinkThreshold = "FileAnalysisMatchAutoLinkThreshold";

    // ---------------------------------------------------------------------------------------------
    // Transactional-email sender identity and the per-recipient throttle (issue #421 Wave 2).
    //
    // SmtpHost, SmtpPort and UseStartTls deliberately DO NOT appear here (Non-Goal 2). SmtpEmailSender
    // connects to the host and THEN authenticates with the configured credentials, so a writable host
    // would harvest the relay credential and every reset token in the message bodies — strictly worse
    // than Email:ClientBaseUrl, which is excluded for the same class of reason.
    // ---------------------------------------------------------------------------------------------

    public const string EmailFromAddress = "EmailFromAddress";
    public const string EmailFromName = "EmailFromName";
    public const string EmailPerRecipientLimit = "EmailPerRecipientLimit";
    public const string EmailPerRecipientWindowMinutes = "EmailPerRecipientWindowMinutes";

    // ---------------------------------------------------------------------------------------------
    // Per-request defensive caps (issue #421 Wave 3). Before this they were invisible: the Contracts,
    // Insurance and PhotoLibrary configuration sections have no appsettings.json entry and no
    // environment plumbing at all, so these were POCO defaults nobody could change without a code
    // edit. Two of the journal caps were not even that — they were `private const` in their services.
    //
    // The two photo caps are TIGHTEN-ONLY: PhotoLimits feeds [MaxLength] on ten request DTOs, and
    // model validation rejects an over-cap request before the service check runs, so raising them
    // would do nothing. See PhotoLimits and the ...Ceiling fields on SystemSettingsDto.
    // ---------------------------------------------------------------------------------------------

    public const string ContractMaxPartiesPerContract = "ContractMaxPartiesPerContract";
    public const string ContractMaxFilesPerContract = "ContractMaxFilesPerContract";
    public const string ContractMaxSummaryContracts = "ContractMaxSummaryContracts";
    public const string InsuranceMaxRenewalsPerPolicy = "InsuranceMaxRenewalsPerPolicy";
    public const string InsuranceMaxFilesPerParent = "InsuranceMaxFilesPerParent";
    public const string PhotoMaxLinksPerKind = "PhotoMaxLinksPerKind";
    public const string PhotoMaxAlbumMembers = "PhotoMaxAlbumMembers";
    public const string JournalEntryMaxLinksPerKind = "JournalEntryMaxLinksPerKind";
    public const string JournalTaskMaxLinksPerKind = "JournalTaskMaxLinksPerKind";

    // ---------------------------------------------------------------------------------------------
    // The upload cap (issue #421 Wave 4). Megabytes, matching the sixteen existing size caps rather
    // than the bytes of the retired `FileStorage:MaxFileSizeBytes` — which is NOT retired, only
    // repurposed: it stays in configuration as the startup TRANSPORT ceiling (Kestrel's request-body
    // limit), and this setting can only be tightened below it. See RequestCapCeilings.
    // ---------------------------------------------------------------------------------------------

    public const string FileStorageMaxUploadMegabytes = "FileStorageMaxUploadMegabytes";

    // ---------------------------------------------------------------------------------------------
    // The last compiled-in tuning constants (issue #434). Three came from appsettings.json, two were
    // POCO defaults on a bound section with no config entry, and ten were `private`/`public const`
    // inside the services that enforce them — unreachable without a code edit, at any cost.
    //
    // Three are SINGLE-DIRECTION, expressed by pinning one end of the [Range] on
    // SystemSettingsUpdate at the shared SystemSettingsDefaults constant (never a second literal):
    // RecurrenceMaxGeneratedOccurrences and ContactVCardMaxRepeatablePropertiesPerEntry are
    // tighten-only (write amplifiers), EmailMaxTrackedRecipients is raise-only (it fails open).
    // ---------------------------------------------------------------------------------------------

    public const string FileAnalysisMaxTokens = "FileAnalysisMaxTokens";
    public const string FileAnalysisMatchMaxVocabulary = "FileAnalysisMatchMaxVocabulary";
    public const string FileAnalysisMatchTimeoutSeconds = "FileAnalysisMatchTimeoutSeconds";
    public const string PhotoMetadataReadMegabytes = "PhotoMetadataReadMegabytes";
    public const string PhotoMetadataExtractionTimeoutSeconds = "PhotoMetadataExtractionTimeoutSeconds";
    public const string CalendarMaxWindowDays = "CalendarMaxWindowDays";
    public const string CalendarMaxEventDurationDays = "CalendarMaxEventDurationDays";
    public const string CalendarIcsMaxAggregateExportRows = "CalendarIcsMaxAggregateExportRows";
    public const string CalendarIcsMaxAggregateOccurrences = "CalendarIcsMaxAggregateOccurrences";
    public const string CalendarIcsMaxAggregateExportWindowDays = "CalendarIcsMaxAggregateExportWindowDays";
    public const string RecurrenceMaxGeneratedOccurrences = "RecurrenceMaxGeneratedOccurrences";
    public const string ContactVCardMaxRepeatablePropertiesPerEntry = "ContactVCardMaxRepeatablePropertiesPerEntry";
    public const string ImportMaxSamplesPerSkipReason = "ImportMaxSamplesPerSkipReason";
    public const string EmailMaxTrackedRecipients = "EmailMaxTrackedRecipients";
    public const string AccountMaxSmartTagsPerAccount = "AccountMaxSmartTagsPerAccount";

    // ---------------------------------------------------------------------------------------------
    // The file-analysis kill switch, model and destination (issue #439) — the last three FileAnalysis
    // values that were still deploy-time only. FileAnalysis:ApiKey deliberately does NOT join them —
    // a bearer credential does not belong in this plaintext store at any point — and issue #445 moved
    // it into the ENCRYPTED secret store instead (SecretSettingKeys.FileAnalysisApiKey). Its
    // destination stays here, which is why the base URL carries the security claim, an https-only
    // shape validator, an audit projection that reduces it to its host, and a non-blocking advisory
    // saying the key travels to whatever host is set.
    //
    // FileAnalysisEnabled is read LIVE and uncached (FileAnalysisSettingsLookup.IsEnabledAsync) and is
    // deliberately absent from the FileAnalysisSettings snapshot, so no caller can consume a cached
    // copy of the kill switch by accident. Model and BaseUrl DO join the snapshot, as NULLABLE members
    // — null means "the stored value could not be used", and the analysis refuses rather than
    // substituting the compiled default (issue #439 §11).
    // ---------------------------------------------------------------------------------------------

    public const string FileAnalysisEnabled = "FileAnalysisEnabled";
    public const string FileAnalysisModel = "FileAnalysisModel";
    public const string FileAnalysisBaseUrl = "FileAnalysisBaseUrl";

    // ---------------------------------------------------------------------------------------------
    // The Subscriptions summary limits (issue #437). Two were `private const` on SubscriptionService
    // and the third did not exist at all — the summary's fetch was unbounded, unlike its Insurance
    // and Contracts siblings. None of the three ever had an appsettings.json key or environment
    // plumbing, so none gets a SystemSettingsConfigAdoption entry (§ Non-Goal 2).
    //
    // All three are read-clamped at BOTH read sites against their SystemSettingsBounds pair, so a
    // hand-edited or restored row outside the [Range] is clamped rather than obeyed.
    // ---------------------------------------------------------------------------------------------

    public const string SubscriptionRenewalWindowDays = "SubscriptionRenewalWindowDays";
    public const string SubscriptionMaxSummaryRenewals = "SubscriptionMaxSummaryRenewals";
    public const string SubscriptionMaxSummarySubscriptions = "SubscriptionMaxSummarySubscriptions";

    public static readonly string[] AllKeys =
    [
        RequireTwoFactor,
        RegistrationRequireAdminApproval,
        EmailRequireConfirmation,
        InsuranceExpiringSoonWindowDays,
        InsuranceMaxSummaryPolicies,
        ContactVCardMaxExportRows,
        ContactVCardMaxImportEntries,
        ContactVCardMaxImportMegabytes,
        ContactVCardMaxExportMegabytes,
        CalendarIcsMaxExportEvents,
        CalendarIcsMaxImportEvents,
        CalendarIcsMaxImportMegabytes,
        CalendarIcsMaxExportMegabytes,
        TaskIcsMaxExportTasks,
        TaskIcsMaxImportTasks,
        TaskIcsMaxImportMegabytes,
        TaskIcsMaxExportMegabytes,
        JournalIcsMaxExportRows,
        JournalIcsMaxImportEntries,
        JournalIcsMaxImportMegabytes,
        JournalIcsMaxExportMegabytes,
        FileAnalysisProcessor,
        FileAnalysisProcessorRegion,
        FileAnalysisLawfulBasis,
        FileAnalysisPrivacyNoticeUrl,
        FileAnalysisMaxFutureTransactionDays,
        FileAnalysisMatchAutoLinkThreshold,
        EmailFromAddress,
        EmailFromName,
        EmailPerRecipientLimit,
        EmailPerRecipientWindowMinutes,
        ContractMaxPartiesPerContract,
        ContractMaxFilesPerContract,
        ContractMaxSummaryContracts,
        InsuranceMaxRenewalsPerPolicy,
        InsuranceMaxFilesPerParent,
        PhotoMaxLinksPerKind,
        PhotoMaxAlbumMembers,
        JournalEntryMaxLinksPerKind,
        JournalTaskMaxLinksPerKind,
        FileStorageMaxUploadMegabytes,
        FileAnalysisMaxTokens,
        FileAnalysisMatchMaxVocabulary,
        FileAnalysisMatchTimeoutSeconds,
        PhotoMetadataReadMegabytes,
        PhotoMetadataExtractionTimeoutSeconds,
        CalendarMaxWindowDays,
        CalendarMaxEventDurationDays,
        CalendarIcsMaxAggregateExportRows,
        CalendarIcsMaxAggregateOccurrences,
        CalendarIcsMaxAggregateExportWindowDays,
        RecurrenceMaxGeneratedOccurrences,
        ContactVCardMaxRepeatablePropertiesPerEntry,
        ImportMaxSamplesPerSkipReason,
        EmailMaxTrackedRecipients,
        AccountMaxSmartTagsPerAccount,
        FileAnalysisEnabled,
        FileAnalysisModel,
        FileAnalysisBaseUrl,
        SubscriptionRenewalWindowDays,
        SubscriptionMaxSummaryRenewals,
        SubscriptionMaxSummarySubscriptions,
    ];

    /// <summary>
    /// The default a missing/unreadable row falls back to lives in
    /// <see cref="Odyssey.Dtos.SystemSettingsDefaults"/>, not here. Only the VALUES moved
    /// (issue #434 §5 item 0): a single-direction setting pins one end of its <c>[Range]</c> at its
    /// shipped default, and that attribute sits on <c>SystemSettingsUpdate</c> in
    /// <c>Odyssey.Dtos.Application</c> — which this project references, so the reverse edge would be
    /// a cycle. This class keeps the key strings, <see cref="AllKeys"/>, and the parse/format helpers.
    /// </summary>
    /// <summary>Parses a count-field's stored string into <c>null</c> (no limit) or a finite value.</summary>
    public static int? ParseCount(string value) =>
        string.Equals(value, SystemSettingsDefaults.Unlimited, StringComparison.OrdinalIgnoreCase)
            ? null
            : int.Parse(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// Non-throwing counterpart of <see cref="ParseCount"/> — <paramref name="result"/> is set (to
    /// <c>null</c> for "unlimited", or the parsed value) and <see langword="true"/> is returned only
    /// when <paramref name="value"/> is well-formed. Used by degraded/fail-safe reads (issue #343
    /// §11, AC 28) where a corrupt stored value must be treated as a fallback case, never an
    /// unhandled exception.
    /// </summary>
    public static bool TryParseCount(string value, out int? result)
    {
        if (string.Equals(value, SystemSettingsDefaults.Unlimited, StringComparison.OrdinalIgnoreCase))
        {
            result = null;
            return true;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            result = parsed;
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>Formats a count field's logical value (<c>null</c> = no limit) back to its stored string.</summary>
    public static string FormatCount(int? value) =>
        value is { } finite ? finite.ToString(CultureInfo.InvariantCulture) : SystemSettingsDefaults.Unlimited;
}
