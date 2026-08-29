using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos;

namespace Odyssey.Api.SystemSettings;

/// <summary>
/// The single declaration of every admin-configurable setting (issue #421 Wave 0). One entry per key,
/// replacing the five parallel per-field blocks <see cref="SystemSettingsService"/> used to carry.
///
/// <para>
/// The claim split is the one issue #349/#343 established, unchanged: policy and cosmetic values take
/// <see cref="PermissionClaims.SystemSettingsUpdate"/>; the authentication perimeter, and the
/// availability knob that import/export <em>size</em> caps represent, take the stricter
/// <see cref="PermissionClaims.SystemSettingsSecurityUpdate"/>. Note the count caps and the size caps
/// deliberately differ from each other on this.
/// </para>
///
/// <para>
/// Accessors are explicit delegates rather than reflection over property names. That is deliberate: a
/// reflection-driven registry would let a renamed <see cref="SystemSettingsUpdate"/> property silently
/// lose its claim, which is the very failure mode the registry exists to prevent. Reflection appears
/// only in the guard tests, where it is the checker rather than the mechanism.
/// </para>
/// </summary>
internal static class SystemSettingsRegistry
{
    /// <summary>Every descriptor, in the order the read DTO presents them.</summary>
    public static readonly IReadOnlyList<SystemSettingDescriptor> All =
    [
        // ── The three authentication-perimeter / security toggles (issue #349) ────────────────────
        // TouchOnPresenceOnly: the five original #349 keys bump UpdatedAt on presence. See the
        // property's remarks — the sixteen newer keys must not, and a test depends on the difference.
        new BoolSetting
        {
            Key = SystemSettingsKeys.RequireTwoFactor,
            FieldName = nameof(SystemSettingsUpdate.RequireTwoFactor),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = Bool(SystemSettingsDefaults.RequireTwoFactor),
            TouchOnPresenceOnly = true,
            Read = r => r.RequireTwoFactor,
            Write = (dto, v) => dto.RequireTwoFactor = v,
        },
        new BoolSetting
        {
            Key = SystemSettingsKeys.RegistrationRequireAdminApproval,
            FieldName = nameof(SystemSettingsUpdate.RegistrationRequireAdminApproval),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = Bool(SystemSettingsDefaults.RegistrationRequireAdminApproval),
            TouchOnPresenceOnly = true,
            Read = r => r.RegistrationRequireAdminApproval,
            Write = (dto, v) => dto.RegistrationRequireAdminApproval = v,
        },
        new BoolSetting
        {
            Key = SystemSettingsKeys.EmailRequireConfirmation,
            FieldName = nameof(SystemSettingsUpdate.EmailRequireConfirmation),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = Bool(SystemSettingsDefaults.EmailRequireConfirmation),
            TouchOnPresenceOnly = true,
            Read = r => r.EmailRequireConfirmation,
            Write = (dto, v) => dto.EmailRequireConfirmation = v,
        },

        // ── Insurance policy knobs (issue #349) — 30s-cached, so a change evicts that entry ───────
        new IntSetting
        {
            Key = SystemSettingsKeys.InsuranceExpiringSoonWindowDays,
            Min = SystemSettingsBounds.InsuranceExpiringSoonWindowDaysMin,
            Max = SystemSettingsBounds.InsuranceExpiringSoonWindowDaysMax,
            FieldName = nameof(SystemSettingsUpdate.InsuranceExpiringSoonWindowDays),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.InsuranceExpiringSoonWindowDays),
            TouchOnPresenceOnly = true,
            CacheKeyToEvict = SystemSettingsService.InsuranceCacheKey,
            Read = r => r.InsuranceExpiringSoonWindowDays,
            Write = (dto, v) => dto.InsuranceExpiringSoonWindowDays = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.InsuranceMaxSummaryPolicies,
            Min = SystemSettingsBounds.InsuranceMaxSummaryPoliciesMin,
            Max = SystemSettingsBounds.InsuranceMaxSummaryPoliciesMax,
            FieldName = nameof(SystemSettingsUpdate.InsuranceMaxSummaryPolicies),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.InsuranceMaxSummaryPolicies),
            TouchOnPresenceOnly = true,
            CacheKeyToEvict = SystemSettingsService.InsuranceCacheKey,
            Read = r => r.InsuranceMaxSummaryPolicies,
            Write = (dto, v) => dto.InsuranceMaxSummaryPolicies = v,
        },

        // ── The sixteen import/export volume caps (issue #343 + follow-ups) ──────────────────────
        // Count caps → update; size caps → security.update (§10 item 4: the availability knob sits
        // behind the stricter claim). All sixteen touch on CHANGE, never on presence.
        ..Capacity(SystemSettingsKeys.ContactVCardMaxExportRows, nameof(SystemSettingsUpdate.ContactVCardMaxExportRows),
            SystemSettingsDefaults.ContactVCardMaxExportRows,
            r => r.ContactVCardMaxExportRows, (dto, v) => dto.ContactVCardMaxExportRows = v),
        ..Capacity(SystemSettingsKeys.ContactVCardMaxImportEntries, nameof(SystemSettingsUpdate.ContactVCardMaxImportEntries),
            SystemSettingsDefaults.ContactVCardMaxImportEntries,
            r => r.ContactVCardMaxImportEntries, (dto, v) => dto.ContactVCardMaxImportEntries = v),
        ..Megabytes(SystemSettingsKeys.ContactVCardMaxImportMegabytes, nameof(SystemSettingsUpdate.ContactVCardMaxImportMegabytes),
            SystemSettingsDefaults.ContactVCardMaxImportMegabytes,
            r => r.ContactVCardMaxImportMegabytes, (dto, v) => dto.ContactVCardMaxImportMegabytes = v),
        ..Megabytes(SystemSettingsKeys.ContactVCardMaxExportMegabytes, nameof(SystemSettingsUpdate.ContactVCardMaxExportMegabytes),
            SystemSettingsDefaults.ContactVCardMaxExportMegabytes,
            r => r.ContactVCardMaxExportMegabytes, (dto, v) => dto.ContactVCardMaxExportMegabytes = v),

        ..Capacity(SystemSettingsKeys.CalendarIcsMaxExportEvents, nameof(SystemSettingsUpdate.CalendarIcsMaxExportEvents),
            Int(SystemSettingsDefaults.CalendarIcsMaxExportEvents),
            r => r.CalendarIcsMaxExportEvents, (dto, v) => dto.CalendarIcsMaxExportEvents = v),
        ..Capacity(SystemSettingsKeys.CalendarIcsMaxImportEvents, nameof(SystemSettingsUpdate.CalendarIcsMaxImportEvents),
            Int(SystemSettingsDefaults.CalendarIcsMaxImportEvents),
            r => r.CalendarIcsMaxImportEvents, (dto, v) => dto.CalendarIcsMaxImportEvents = v),
        ..Megabytes(SystemSettingsKeys.CalendarIcsMaxImportMegabytes, nameof(SystemSettingsUpdate.CalendarIcsMaxImportMegabytes),
            SystemSettingsDefaults.CalendarIcsMaxImportMegabytes,
            r => r.CalendarIcsMaxImportMegabytes, (dto, v) => dto.CalendarIcsMaxImportMegabytes = v),
        ..Megabytes(SystemSettingsKeys.CalendarIcsMaxExportMegabytes, nameof(SystemSettingsUpdate.CalendarIcsMaxExportMegabytes),
            SystemSettingsDefaults.CalendarIcsMaxExportMegabytes,
            r => r.CalendarIcsMaxExportMegabytes, (dto, v) => dto.CalendarIcsMaxExportMegabytes = v),

        ..Capacity(SystemSettingsKeys.TaskIcsMaxExportTasks, nameof(SystemSettingsUpdate.TaskIcsMaxExportTasks),
            Int(SystemSettingsDefaults.TaskIcsMaxExportTasks),
            r => r.TaskIcsMaxExportTasks, (dto, v) => dto.TaskIcsMaxExportTasks = v),
        ..Capacity(SystemSettingsKeys.TaskIcsMaxImportTasks, nameof(SystemSettingsUpdate.TaskIcsMaxImportTasks),
            Int(SystemSettingsDefaults.TaskIcsMaxImportTasks),
            r => r.TaskIcsMaxImportTasks, (dto, v) => dto.TaskIcsMaxImportTasks = v),
        ..Megabytes(SystemSettingsKeys.TaskIcsMaxImportMegabytes, nameof(SystemSettingsUpdate.TaskIcsMaxImportMegabytes),
            SystemSettingsDefaults.TaskIcsMaxImportMegabytes,
            r => r.TaskIcsMaxImportMegabytes, (dto, v) => dto.TaskIcsMaxImportMegabytes = v),
        ..Megabytes(SystemSettingsKeys.TaskIcsMaxExportMegabytes, nameof(SystemSettingsUpdate.TaskIcsMaxExportMegabytes),
            SystemSettingsDefaults.TaskIcsMaxExportMegabytes,
            r => r.TaskIcsMaxExportMegabytes, (dto, v) => dto.TaskIcsMaxExportMegabytes = v),

        ..Capacity(SystemSettingsKeys.JournalIcsMaxExportRows, nameof(SystemSettingsUpdate.JournalIcsMaxExportRows),
            Int(SystemSettingsDefaults.JournalIcsMaxExportRows),
            r => r.JournalIcsMaxExportRows, (dto, v) => dto.JournalIcsMaxExportRows = v),
        ..Capacity(SystemSettingsKeys.JournalIcsMaxImportEntries, nameof(SystemSettingsUpdate.JournalIcsMaxImportEntries),
            Int(SystemSettingsDefaults.JournalIcsMaxImportEntries),
            r => r.JournalIcsMaxImportEntries, (dto, v) => dto.JournalIcsMaxImportEntries = v),
        ..Megabytes(SystemSettingsKeys.JournalIcsMaxImportMegabytes, nameof(SystemSettingsUpdate.JournalIcsMaxImportMegabytes),
            SystemSettingsDefaults.JournalIcsMaxImportMegabytes,
            r => r.JournalIcsMaxImportMegabytes, (dto, v) => dto.JournalIcsMaxImportMegabytes = v),
        ..Megabytes(SystemSettingsKeys.JournalIcsMaxExportMegabytes, nameof(SystemSettingsUpdate.JournalIcsMaxExportMegabytes),
            SystemSettingsDefaults.JournalIcsMaxExportMegabytes,
            r => r.JournalIcsMaxExportMegabytes, (dto, v) => dto.JournalIcsMaxExportMegabytes = v),

        // ── AI file-analysis policy and processor disclosure (issue #421 Wave 1) ──────────────────
        // The four disclosure strings carry legal weight — they are what the consent gate shows the
        // user under GDPR Art. 13 — so they sit behind the stricter claim and are therefore audited by
        // the derived AuditChanges rule. The two tuning values are ordinary policy.
        new StringSetting
        {
            Key = SystemSettingsKeys.FileAnalysisProcessor,
            FieldName = nameof(SystemSettingsUpdate.FileAnalysisProcessor),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = SystemSettingsDefaults.FileAnalysisProcessor,
            CacheKeyToEvict = FileAnalysisSettingsLookup.CacheKey,
            MaxLength = 128,
            Read = r => r.FileAnalysisProcessor,
            Write = (dto, v) => dto.FileAnalysisProcessor = v,
            Advise = SettingAdvisories.ProcessorMatchesBaseUrl,
        },
        new StringSetting
        {
            Key = SystemSettingsKeys.FileAnalysisProcessorRegion,
            FieldName = nameof(SystemSettingsUpdate.FileAnalysisProcessorRegion),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = SystemSettingsDefaults.FileAnalysisProcessorRegion,
            CacheKeyToEvict = FileAnalysisSettingsLookup.CacheKey,
            MaxLength = 128,
            Read = r => r.FileAnalysisProcessorRegion,
            Write = (dto, v) => dto.FileAnalysisProcessorRegion = v,
        },
        new StringSetting
        {
            Key = SystemSettingsKeys.FileAnalysisLawfulBasis,
            FieldName = nameof(SystemSettingsUpdate.FileAnalysisLawfulBasis),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = SystemSettingsDefaults.FileAnalysisLawfulBasis,
            CacheKeyToEvict = FileAnalysisSettingsLookup.CacheKey,
            MaxLength = 128,
            Read = r => r.FileAnalysisLawfulBasis,
            Write = (dto, v) => dto.FileAnalysisLawfulBasis = v,
        },
        new StringSetting
        {
            Key = SystemSettingsKeys.FileAnalysisPrivacyNoticeUrl,
            FieldName = nameof(SystemSettingsUpdate.FileAnalysisPrivacyNoticeUrl),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = SystemSettingsDefaults.FileAnalysisPrivacyNoticeUrl,
            CacheKeyToEvict = FileAnalysisSettingsLookup.CacheKey,
            MaxLength = 256,
            Validator = PrivacyNoticeUrl.Validate,
            Read = r => r.FileAnalysisPrivacyNoticeUrl,
            Write = (dto, v) => dto.FileAnalysisPrivacyNoticeUrl = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.FileAnalysisMaxFutureTransactionDays,
            Min = SystemSettingsBounds.FileAnalysisMaxFutureTransactionDaysMin,
            Max = SystemSettingsBounds.FileAnalysisMaxFutureTransactionDaysMax,
            FieldName = nameof(SystemSettingsUpdate.FileAnalysisMaxFutureTransactionDays),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.FileAnalysisMaxFutureTransactionDays),
            CacheKeyToEvict = FileAnalysisSettingsLookup.CacheKey,
            Read = r => r.FileAnalysisMaxFutureTransactionDays,
            Write = (dto, v) => dto.FileAnalysisMaxFutureTransactionDays = v,
        },
        new DecimalSetting
        {
            Key = SystemSettingsKeys.FileAnalysisMatchAutoLinkThreshold,
            FieldName = nameof(SystemSettingsUpdate.FileAnalysisMatchAutoLinkThreshold),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = SystemSettingsDefaults.FileAnalysisMatchAutoLinkThreshold
                .ToString(DecimalSetting.StorageFormat, System.Globalization.CultureInfo.InvariantCulture),
            CacheKeyToEvict = FileAnalysisSettingsLookup.CacheKey,
            Read = r => r.FileAnalysisMatchAutoLinkThreshold,
            Write = (dto, v) => dto.FileAnalysisMatchAutoLinkThreshold = v,
        },

        // ── Transactional email (issue #421 Wave 2) ───────────────────────────────────────────────
        // All four take the STRICTER claim. The sender identity is what recipients see and what the
        // relay authorises; the throttle is a security control on the anonymous mail path. The SMTP
        // transport fields are absent by design (Non-Goal 2) — a writable host harvests the relay
        // credential and every reset token, because the sender connects and then authenticates.
        new StringSetting
        {
            Key = SystemSettingsKeys.EmailFromAddress,
            FieldName = nameof(SystemSettingsUpdate.EmailFromAddress),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = SystemSettingsDefaults.EmailFromAddress,
            MaxLength = 256,
            Validator = EmailSenderIdentity.ValidateFromAddress,
            Read = r => r.EmailFromAddress,
            Write = (dto, v) => dto.EmailFromAddress = v,
        },
        new StringSetting
        {
            Key = SystemSettingsKeys.EmailFromName,
            FieldName = nameof(SystemSettingsUpdate.EmailFromName),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = SystemSettingsDefaults.EmailFromName,
            MaxLength = 128,
            Read = r => r.EmailFromName,
            Write = (dto, v) => dto.EmailFromName = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.EmailPerRecipientLimit,
            Min = SystemSettingsBounds.EmailPerRecipientLimitMin,
            Max = SystemSettingsBounds.EmailPerRecipientLimitMax,
            FieldName = nameof(SystemSettingsUpdate.EmailPerRecipientLimit),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = Int(SystemSettingsDefaults.EmailPerRecipientLimit),
            Read = r => r.EmailPerRecipientLimit,
            Write = (dto, v) => dto.EmailPerRecipientLimit = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.EmailPerRecipientWindowMinutes,
            Min = SystemSettingsBounds.EmailPerRecipientWindowMinutesMin,
            Max = SystemSettingsBounds.EmailPerRecipientWindowMinutesMax,
            FieldName = nameof(SystemSettingsUpdate.EmailPerRecipientWindowMinutes),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = Int(SystemSettingsDefaults.EmailPerRecipientWindowMinutes),
            Read = r => r.EmailPerRecipientWindowMinutes,
            Write = (dto, v) => dto.EmailPerRecipientWindowMinutes = v,
        },

        // ── Per-request defensive caps (issue #421 Wave 3) ────────────────────────────────────────
        // All nine take the ORDINARY write claim. They are availability bounds, so appsec argued for
        // the stricter one; the reasoning for keeping them here is in issue #421 §10.10 — each is
        // bounded by [Range(1, 100000)], the two with real leverage are additionally ceilinged by
        // their DTO annotations, and every holder of either claim is Admin today, so the split is
        // defence-in-depth against a future role rather than a live boundary.
        //
        // Three cache keys, not one: the caps span two domain projects, and a lookup interface lives
        // in the project that consumes it so that project's tests can fake it. Contracts and Insurance
        // are Odyssey.Core.Finance (insurance reusing the existing entry, so one eviction covers it);
        // photos and journal are Odyssey.Core.Journal.
        new IntSetting
        {
            Key = SystemSettingsKeys.ContractMaxPartiesPerContract,
            Min = SystemSettingsBounds.ContractMaxPartiesPerContractMin,
            Max = SystemSettingsBounds.ContractMaxPartiesPerContractMax,
            FieldName = nameof(SystemSettingsUpdate.ContractMaxPartiesPerContract),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.ContractMaxPartiesPerContract),
            CacheKeyToEvict = SystemSettingsService.FinanceCapsCacheKey,
            Read = r => r.ContractMaxPartiesPerContract,
            Write = (dto, v) => dto.ContractMaxPartiesPerContract = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.ContractMaxFilesPerContract,
            Min = SystemSettingsBounds.ContractMaxFilesPerContractMin,
            Max = SystemSettingsBounds.ContractMaxFilesPerContractMax,
            FieldName = nameof(SystemSettingsUpdate.ContractMaxFilesPerContract),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.ContractMaxFilesPerContract),
            CacheKeyToEvict = SystemSettingsService.FinanceCapsCacheKey,
            Read = r => r.ContractMaxFilesPerContract,
            Write = (dto, v) => dto.ContractMaxFilesPerContract = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.ContractMaxSummaryContracts,
            Min = SystemSettingsBounds.ContractMaxSummaryContractsMin,
            Max = SystemSettingsBounds.ContractMaxSummaryContractsMax,
            FieldName = nameof(SystemSettingsUpdate.ContractMaxSummaryContracts),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.ContractMaxSummaryContracts),
            CacheKeyToEvict = SystemSettingsService.FinanceCapsCacheKey,
            Read = r => r.ContractMaxSummaryContracts,
            Write = (dto, v) => dto.ContractMaxSummaryContracts = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.InsuranceMaxRenewalsPerPolicy,
            Min = SystemSettingsBounds.InsuranceMaxRenewalsPerPolicyMin,
            Max = SystemSettingsBounds.InsuranceMaxRenewalsPerPolicyMax,
            FieldName = nameof(SystemSettingsUpdate.InsuranceMaxRenewalsPerPolicy),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.InsuranceMaxRenewalsPerPolicy),
            CacheKeyToEvict = SystemSettingsService.InsuranceCacheKey,
            Read = r => r.InsuranceMaxRenewalsPerPolicy,
            Write = (dto, v) => dto.InsuranceMaxRenewalsPerPolicy = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.InsuranceMaxFilesPerParent,
            Min = SystemSettingsBounds.InsuranceMaxFilesPerParentMin,
            Max = SystemSettingsBounds.InsuranceMaxFilesPerParentMax,
            FieldName = nameof(SystemSettingsUpdate.InsuranceMaxFilesPerParent),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.InsuranceMaxFilesPerParent),
            CacheKeyToEvict = SystemSettingsService.InsuranceCacheKey,
            Read = r => r.InsuranceMaxFilesPerParent,
            Write = (dto, v) => dto.InsuranceMaxFilesPerParent = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.PhotoMaxLinksPerKind,
            Min = SystemSettingsBounds.PhotoMaxLinksPerKindMin,
            Max = SystemSettingsBounds.PhotoMaxLinksPerKindMax,
            FieldName = nameof(SystemSettingsUpdate.PhotoMaxLinksPerKind),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.PhotoMaxLinksPerKind),
            CacheKeyToEvict = JournalLimitsLookup.CacheKey,
            Validator = (value, _) => RequestCapCeilings.ValidatePhotoLinksPerKind(value),
            Read = r => r.PhotoMaxLinksPerKind,
            Write = (dto, v) => dto.PhotoMaxLinksPerKind = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.PhotoMaxAlbumMembers,
            Min = SystemSettingsBounds.PhotoMaxAlbumMembersMin,
            Max = SystemSettingsBounds.PhotoMaxAlbumMembersMax,
            FieldName = nameof(SystemSettingsUpdate.PhotoMaxAlbumMembers),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.PhotoMaxAlbumMembers),
            CacheKeyToEvict = JournalLimitsLookup.CacheKey,
            Validator = (value, _) => RequestCapCeilings.ValidatePhotoAlbumMembers(value),
            Read = r => r.PhotoMaxAlbumMembers,
            Write = (dto, v) => dto.PhotoMaxAlbumMembers = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.JournalEntryMaxLinksPerKind,
            Min = SystemSettingsBounds.JournalEntryMaxLinksPerKindMin,
            Max = SystemSettingsBounds.JournalEntryMaxLinksPerKindMax,
            FieldName = nameof(SystemSettingsUpdate.JournalEntryMaxLinksPerKind),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.JournalEntryMaxLinksPerKind),
            CacheKeyToEvict = JournalLimitsLookup.CacheKey,
            Read = r => r.JournalEntryMaxLinksPerKind,
            Write = (dto, v) => dto.JournalEntryMaxLinksPerKind = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.JournalTaskMaxLinksPerKind,
            Min = SystemSettingsBounds.JournalTaskMaxLinksPerKindMin,
            Max = SystemSettingsBounds.JournalTaskMaxLinksPerKindMax,
            FieldName = nameof(SystemSettingsUpdate.JournalTaskMaxLinksPerKind),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.JournalTaskMaxLinksPerKind),
            CacheKeyToEvict = JournalLimitsLookup.CacheKey,
            Read = r => r.JournalTaskMaxLinksPerKind,
            Write = (dto, v) => dto.JournalTaskMaxLinksPerKind = v,
        },
        // The upload cap (issue #421 Wave 4). Its own cache key: the consumers are the file-validation
        // path and the transport middleware, neither of which shares an eviction with the finance or
        // journal caps.
        new IntSetting
        {
            Key = SystemSettingsKeys.FileStorageMaxUploadMegabytes,
            Min = SystemSettingsBounds.FileStorageMaxUploadMegabytesMin,
            Max = SystemSettingsBounds.FileStorageMaxUploadMegabytesMax,
            FieldName = nameof(SystemSettingsUpdate.FileStorageMaxUploadMegabytes),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = Int(SystemSettingsDefaults.FileStorageMaxUploadMegabytes),
            CacheKeyToEvict = UploadLimitsLookup.CacheKey,
            Validator = (value, ceilings) => ceilings.ValidateUploadMegabytes(value),
            Read = r => r.FileStorageMaxUploadMegabytes,
            Write = (dto, v) => dto.FileStorageMaxUploadMegabytes = v,
        },

        // ── The last compiled-in tuning constants (issue #434) ────────────────────────────────────
        //
        // Two of the fifteen take the STRICTER claim, and in both cases the deciding argument is that
        // AuditChanges is DERIVED from it (SystemSettingDescriptor.AuditChanges): FileAnalysisMaxTokens
        // is a direct third-party spend lever, and an unaudited spend lever is worse than an
        // over-classified one; PhotoMetadataReadMegabytes is the tenth megabyte row, and all nine
        // existing ones are on the security claim (every one of them also being a resource bound, which
        // is why "resource bound, not abuse control" does not distinguish it from its neighbours).
        //
        // NO Validator on any of them. Their bounds are static const ints, so they live in [Range] on
        // the write DTO — model validation runs first, so a RequestCapCeilings validator repeating the
        // same number could never fire (§9). The photo/upload validators above stay, because those
        // ceilings are runtime- or cross-assembly-derived and cannot be written into an attribute.
        new IntSetting
        {
            Key = SystemSettingsKeys.FileAnalysisMaxTokens,
            Min = SystemSettingsBounds.FileAnalysisMaxTokensMin,
            Max = SystemSettingsBounds.FileAnalysisMaxTokensMax,
            FieldName = nameof(SystemSettingsUpdate.FileAnalysisMaxTokens),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = Int(SystemSettingsDefaults.FileAnalysisMaxTokens),
            CacheKeyToEvict = FileAnalysisSettingsLookup.CacheKey,
            Read = r => r.FileAnalysisMaxTokens,
            Write = (dto, v) => dto.FileAnalysisMaxTokens = v,
            Advise = SettingAdvisories.AboveDefault(
                dto => dto.FileAnalysisMaxTokens, SystemSettingsDefaults.FileAnalysisMaxTokens,
                "Each analysis may now return up to this many tokens, and the provider bills per token, "
                + "so this raises the cost ceiling of every extraction and every match call."),
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.FileAnalysisMatchMaxVocabulary,
            Min = SystemSettingsBounds.FileAnalysisMatchMaxVocabularyMin,
            Max = SystemSettingsBounds.FileAnalysisMatchMaxVocabularyMax,
            FieldName = nameof(SystemSettingsUpdate.FileAnalysisMatchMaxVocabulary),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.FileAnalysisMatchMaxVocabulary),
            CacheKeyToEvict = FileAnalysisSettingsLookup.CacheKey,
            Read = r => r.FileAnalysisMatchMaxVocabulary,
            Write = (dto, v) => dto.FileAnalysisMatchMaxVocabulary = v,
            Advise = SettingAdvisories.AboveDefault(
                dto => dto.FileAnalysisMatchMaxVocabulary, SystemSettingsDefaults.FileAnalysisMatchMaxVocabulary,
                "Every name in the vocabulary is sent to the provider on each match call, so a larger "
                + "list means a larger request and more tokens billed per match."),
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.FileAnalysisMatchTimeoutSeconds,
            Min = SystemSettingsBounds.FileAnalysisMatchTimeoutSecondsMin,
            Max = SystemSettingsBounds.FileAnalysisMatchTimeoutSecondsMax,
            FieldName = nameof(SystemSettingsUpdate.FileAnalysisMatchTimeoutSeconds),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.FileAnalysisMatchTimeoutSeconds),
            CacheKeyToEvict = FileAnalysisSettingsLookup.CacheKey,
            Read = r => r.FileAnalysisMatchTimeoutSeconds,
            Write = (dto, v) => dto.FileAnalysisMatchTimeoutSeconds = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.PhotoMetadataReadMegabytes,
            Min = SystemSettingsBounds.PhotoMetadataReadMegabytesMin,
            Max = SystemSettingsBounds.PhotoMetadataReadMegabytesMax,
            FieldName = nameof(SystemSettingsUpdate.PhotoMetadataReadMegabytes),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = Int(SystemSettingsDefaults.PhotoMetadataReadMegabytes),
            CacheKeyToEvict = JournalLimitsLookup.CacheKey,
            Read = r => r.PhotoMetadataReadMegabytes,
            Write = (dto, v) => dto.PhotoMetadataReadMegabytes = v,
            Advise = SettingAdvisories.AboveDefault(
                dto => dto.PhotoMetadataReadMegabytes, SystemSettingsDefaults.PhotoMetadataReadMegabytes,
                "Metadata extraction materialises a full byte array of this size for every photo "
                + "uploaded, so this is a per-upload memory multiplier. 16 MB is also MariaDB's default "
                + "max_allowed_packet — beyond it the prefix read simply returns less."),
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.PhotoMetadataExtractionTimeoutSeconds,
            Min = SystemSettingsBounds.PhotoMetadataExtractionTimeoutSecondsMin,
            Max = SystemSettingsBounds.PhotoMetadataExtractionTimeoutSecondsMax,
            FieldName = nameof(SystemSettingsUpdate.PhotoMetadataExtractionTimeoutSeconds),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.PhotoMetadataExtractionTimeoutSeconds),
            CacheKeyToEvict = JournalLimitsLookup.CacheKey,
            Read = r => r.PhotoMetadataExtractionTimeoutSeconds,
            Write = (dto, v) => dto.PhotoMetadataExtractionTimeoutSeconds = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.CalendarMaxWindowDays,
            Min = SystemSettingsBounds.CalendarMaxWindowDaysMin,
            Max = SystemSettingsBounds.CalendarMaxWindowDaysMax,
            FieldName = nameof(SystemSettingsUpdate.CalendarMaxWindowDays),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.CalendarMaxWindowDays),
            CacheKeyToEvict = JournalLimitsLookup.CacheKey,
            Read = r => r.CalendarMaxWindowDays,
            Write = (dto, v) => dto.CalendarMaxWindowDays = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.CalendarMaxEventDurationDays,
            Min = SystemSettingsBounds.CalendarMaxEventDurationDaysMin,
            Max = SystemSettingsBounds.CalendarMaxEventDurationDaysMax,
            FieldName = nameof(SystemSettingsUpdate.CalendarMaxEventDurationDays),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.CalendarMaxEventDurationDays),
            CacheKeyToEvict = JournalLimitsLookup.CacheKey,
            Read = r => r.CalendarMaxEventDurationDays,
            Write = (dto, v) => dto.CalendarMaxEventDurationDays = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.CalendarIcsMaxAggregateExportRows,
            Min = SystemSettingsBounds.CalendarIcsMaxAggregateExportRowsMin,
            Max = SystemSettingsBounds.CalendarIcsMaxAggregateExportRowsMax,
            FieldName = nameof(SystemSettingsUpdate.CalendarIcsMaxAggregateExportRows),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.CalendarIcsMaxAggregateExportRows),
            CacheKeyToEvict = ImportExportLimitsLookup.CacheKey,
            Read = r => r.CalendarIcsMaxAggregateExportRows,
            Write = (dto, v) => dto.CalendarIcsMaxAggregateExportRows = v,
            Advise = SettingAdvisories.AboveDefault(
                dto => dto.CalendarIcsMaxAggregateExportRows, SystemSettingsDefaults.CalendarIcsMaxAggregateExportRows,
                "The aggregate export path materialises every fetched row, and up to four exports can "
                + "run at once, so this multiplies peak memory on that surface."),
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.CalendarIcsMaxAggregateOccurrences,
            Min = SystemSettingsBounds.CalendarIcsMaxAggregateOccurrencesMin,
            Max = SystemSettingsBounds.CalendarIcsMaxAggregateOccurrencesMax,
            FieldName = nameof(SystemSettingsUpdate.CalendarIcsMaxAggregateOccurrences),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.CalendarIcsMaxAggregateOccurrences),
            CacheKeyToEvict = ImportExportLimitsLookup.CacheKey,
            Read = r => r.CalendarIcsMaxAggregateOccurrences,
            Write = (dto, v) => dto.CalendarIcsMaxAggregateOccurrences = v,
            Advise = SettingAdvisories.AboveDefault(
                dto => dto.CalendarIcsMaxAggregateOccurrences, SystemSettingsDefaults.CalendarIcsMaxAggregateOccurrences,
                "Every occurrence is held in memory while an import runs, so a large value can make a "
                + "single import slow or exhaust memory."),
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.CalendarIcsMaxAggregateExportWindowDays,
            Min = SystemSettingsBounds.CalendarIcsMaxAggregateExportWindowDaysMin,
            Max = SystemSettingsBounds.CalendarIcsMaxAggregateExportWindowDaysMax,
            FieldName = nameof(SystemSettingsUpdate.CalendarIcsMaxAggregateExportWindowDays),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.CalendarIcsMaxAggregateExportWindowDays),
            CacheKeyToEvict = ImportExportLimitsLookup.CacheKey,
            Read = r => r.CalendarIcsMaxAggregateExportWindowDays,
            Write = (dto, v) => dto.CalendarIcsMaxAggregateExportWindowDays = v,
        },
        // Tighten-only. No advisory: it cannot be raised at all, so there is no cost to warn about.
        new IntSetting
        {
            Key = SystemSettingsKeys.RecurrenceMaxGeneratedOccurrences,
            Min = SystemSettingsBounds.RecurrenceMaxGeneratedOccurrencesMin,
            Max = SystemSettingsBounds.RecurrenceMaxGeneratedOccurrencesMax,
            FieldName = nameof(SystemSettingsUpdate.RecurrenceMaxGeneratedOccurrences),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences),
            CacheKeyToEvict = JournalLimitsLookup.CacheKey,
            Read = r => r.RecurrenceMaxGeneratedOccurrences,
            Write = (dto, v) => dto.RecurrenceMaxGeneratedOccurrences = v,
        },
        // Tighten-only, same reason.
        new IntSetting
        {
            Key = SystemSettingsKeys.ContactVCardMaxRepeatablePropertiesPerEntry,
            Min = SystemSettingsBounds.ContactVCardMaxRepeatablePropertiesPerEntryMin,
            Max = SystemSettingsBounds.ContactVCardMaxRepeatablePropertiesPerEntryMax,
            FieldName = nameof(SystemSettingsUpdate.ContactVCardMaxRepeatablePropertiesPerEntry),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry),
            CacheKeyToEvict = ImportExportLimitsLookup.CacheKey,
            Read = r => r.ContactVCardMaxRepeatablePropertiesPerEntry,
            Write = (dto, v) => dto.ContactVCardMaxRepeatablePropertiesPerEntry = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.ImportMaxSamplesPerSkipReason,
            Min = SystemSettingsBounds.ImportMaxSamplesPerSkipReasonMin,
            Max = SystemSettingsBounds.ImportMaxSamplesPerSkipReasonMax,
            FieldName = nameof(SystemSettingsUpdate.ImportMaxSamplesPerSkipReason),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.ImportMaxSamplesPerSkipReason),
            CacheKeyToEvict = ImportExportLimitsLookup.CacheKey,
            Read = r => r.ImportMaxSamplesPerSkipReason,
            Write = (dto, v) => dto.ImportMaxSamplesPerSkipReason = v,
            Advise = SettingAdvisories.AboveDefault(
                dto => dto.ImportMaxSamplesPerSkipReason, SystemSettingsDefaults.ImportMaxSamplesPerSkipReason,
                "Samples are carried back in the import summary, so this grows the response payload of "
                + "an import that skips a lot of rows. Skip COUNTS are unaffected — they are always exact."),
        },
        // No cache key: the mail throttle reads live per send, exactly as the two Wave 2 throttle
        // settings do, because a lowered limit under active abuse must bind on the very next send.
        new IntSetting
        {
            Key = SystemSettingsKeys.EmailMaxTrackedRecipients,
            Min = SystemSettingsBounds.EmailMaxTrackedRecipientsMin,
            Max = SystemSettingsBounds.EmailMaxTrackedRecipientsMax,
            FieldName = nameof(SystemSettingsUpdate.EmailMaxTrackedRecipients),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = Int(SystemSettingsDefaults.EmailMaxTrackedRecipients),
            Read = r => r.EmailMaxTrackedRecipients,
            Write = (dto, v) => dto.EmailMaxTrackedRecipients = v,
        },
        // ── The file-analysis kill switch, model and destination (issue #439) ────────────────────
        //
        // All three take the SECURITY claim, so all three are audited by the derived AuditChanges rule.
        // The switch authorises transferring personal data to a third party; the model is stamped on
        // every job; the base URL is where the document and the configured API key actually go. There
        // is no weaker reading of any of them.
        //
        // All three declare FileAnalysisSettingsLookup.CacheKey. FileAnalysisEnabled is read LIVE and
        // is not on the cached snapshot at all, so its eviction is a no-op — declared anyway because a
        // null here reads as "this key is cached nowhere", which becomes false the moment somebody adds
        // it to a snapshot. TouchOnPresenceOnly stays false, matching every key added since #343.
        new BoolSetting
        {
            Key = SystemSettingsKeys.FileAnalysisEnabled,
            FieldName = nameof(SystemSettingsUpdate.FileAnalysisEnabled),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = Bool(SystemSettingsDefaults.FileAnalysisEnabled),
            CacheKeyToEvict = FileAnalysisSettingsLookup.CacheKey,
            Read = r => r.FileAnalysisEnabled,
            Write = (dto, v) => dto.FileAnalysisEnabled = v,
            Advise = SettingAdvisories.AnalysisEnabled,
        },
        new StringSetting
        {
            Key = SystemSettingsKeys.FileAnalysisModel,
            FieldName = nameof(SystemSettingsUpdate.FileAnalysisModel),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = SystemSettingsDefaults.FileAnalysisModel,
            CacheKeyToEvict = FileAnalysisSettingsLookup.CacheKey,
            MaxLength = 128,
            Read = r => r.FileAnalysisModel,
            Write = (dto, v) => dto.FileAnalysisModel = v,
            Advise = SettingAdvisories.ModelAwayFromDefault,
        },
        // The highest-consequence setting in the store, and the only one carrying an AuditProjection.
        // The API key stays a deploy-time secret attached to the outbound client, so repointing this
        // sends that key to the new host — accepted (Non-Goal 1), and the reason for the claim, the
        // audit line, the https-only validator, the host-only projection and the row advisory.
        new StringSetting
        {
            Key = SystemSettingsKeys.FileAnalysisBaseUrl,
            FieldName = nameof(SystemSettingsUpdate.FileAnalysisBaseUrl),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = SystemSettingsDefaults.FileAnalysisBaseUrl,
            CacheKeyToEvict = FileAnalysisSettingsLookup.CacheKey,
            MaxLength = 256,
            Validator = FileAnalysisBaseUrlRule.Validate,
            Canonicalize = FileAnalysisBaseUrlRule.Canonicalize,
            AuditProjection = FileAnalysisBaseUrlRule.Host,
            Read = r => r.FileAnalysisBaseUrl,
            Write = (dto, v) => dto.FileAnalysisBaseUrl = v,
            Advise = SettingAdvisories.BaseUrlAwayFromDefault,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.AccountMaxSmartTagsPerAccount,
            Min = SystemSettingsBounds.AccountMaxSmartTagsPerAccountMin,
            Max = SystemSettingsBounds.AccountMaxSmartTagsPerAccountMax,
            FieldName = nameof(SystemSettingsUpdate.AccountMaxSmartTagsPerAccount),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.AccountMaxSmartTagsPerAccount),
            CacheKeyToEvict = AccountLimitsLookup.CacheKey,
            Read = r => r.AccountMaxSmartTagsPerAccount,
            Write = (dto, v) => dto.AccountMaxSmartTagsPerAccount = v,
        },

        // ── The Subscriptions summary limits (issue #437) ─────────────────────────────────────────
        //
        // All three take the ORDINARY write claim, matching the three analogous keys above
        // (InsuranceExpiringSoonWindowDays, InsuranceMaxSummaryPolicies, ContractMaxSummaryContracts):
        // the established split puts the authentication perimeter and the import/export SIZE caps
        // behind the stricter claim, and these are display bounds. The honest limit is that both
        // system-settings claims are Admin-only today, so the choice's only operational effect is the
        // derived AuditChanges — tracked as issue #438, which cannot ride along here because
        // decoupling AuditChanges from the claim would require weakening the very assertion AC 15
        // forbids weakening.
        //
        // One shared cache key, and NOT the insurance entry: SystemSettingDescriptor.CacheKeyToEvict
        // is a single string, so a shared entry would make a subscriptions change evict the insurance
        // settings and vice versa. TouchOnPresenceOnly stays false — that is reserved for the five
        // original #349 keys.
        new IntSetting
        {
            Key = SystemSettingsKeys.SubscriptionRenewalWindowDays,
            Min = SystemSettingsBounds.SubscriptionRenewalWindowDaysMin,
            Max = SystemSettingsBounds.SubscriptionRenewalWindowDaysMax,
            FieldName = nameof(SystemSettingsUpdate.SubscriptionRenewalWindowDays),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.SubscriptionRenewalWindowDays),
            CacheKeyToEvict = SystemSettingsService.SubscriptionCacheKey,
            Read = r => r.SubscriptionRenewalWindowDays,
            Write = (dto, v) => dto.SubscriptionRenewalWindowDays = v,
        },
        // The only one of the three with a cost advisory. AboveDefault already covers PAYLOAD/RENDER
        // cost, not only memory, CPU and third-party spend — ImportMaxSamplesPerSkipReason above is
        // that exact shape.
        new IntSetting
        {
            Key = SystemSettingsKeys.SubscriptionMaxSummaryRenewals,
            Min = SystemSettingsBounds.SubscriptionMaxSummaryRenewalsMin,
            Max = SystemSettingsBounds.SubscriptionMaxSummaryRenewalsMax,
            FieldName = nameof(SystemSettingsUpdate.SubscriptionMaxSummaryRenewals),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.SubscriptionMaxSummaryRenewals),
            CacheKeyToEvict = SystemSettingsService.SubscriptionCacheKey,
            Read = r => r.SubscriptionMaxSummaryRenewals,
            Write = (dto, v) => dto.SubscriptionMaxSummaryRenewals = v,
            Advise = SettingAdvisories.AboveDefault(
                dto => dto.SubscriptionMaxSummaryRenewals, SystemSettingsDefaults.SubscriptionMaxSummaryRenewals,
                "Each renewal is rendered as its own block above the list."),
        },
        // No cost advisory, deliberately: the value closest to today's behaviour is the UNBOUNDED one,
        // so an "above the shipped default" advisory here would fire on the direction that changes
        // least. The truncation consequence is stated on the row's description instead.
        new IntSetting
        {
            Key = SystemSettingsKeys.SubscriptionMaxSummarySubscriptions,
            Min = SystemSettingsBounds.SubscriptionMaxSummarySubscriptionsMin,
            Max = SystemSettingsBounds.SubscriptionMaxSummarySubscriptionsMax,
            FieldName = nameof(SystemSettingsUpdate.SubscriptionMaxSummarySubscriptions),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.SubscriptionMaxSummarySubscriptions),
            CacheKeyToEvict = SystemSettingsService.SubscriptionCacheKey,
            Read = r => r.SubscriptionMaxSummarySubscriptions,
            Write = (dto, v) => dto.SubscriptionMaxSummarySubscriptions = v,
        },
    ];

    /// <summary>Descriptors by <see cref="SystemSettingDescriptor.Key"/>.</summary>
    public static readonly IReadOnlyDictionary<string, SystemSettingDescriptor> ByKey =
        All.ToDictionary(descriptor => descriptor.Key, StringComparer.Ordinal);

    /// <summary>
    /// The default a missing row falls back to. Throws on an unknown key, exactly as the switch it
    /// replaced did — a key outside the registry is a programming error, not a runtime condition.
    /// </summary>
    public static string DefaultValueFor(string key) =>
        ByKey.TryGetValue(key, out var descriptor)
            ? descriptor.DefaultValue
            : throw new InvalidOperationException($"Unknown system setting key '{key}'.");

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Int(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // The two import/export factories exist only to keep the sixteen entries above readable — the caps
    // differ solely in key, field, default and claim, so spelling out sixteen full initialisers would
    // bury the one thing worth reading (which claim each carries). They return a single-element array
    // so the entries can sit inside the collection expression above via a spread.
    private static SystemSettingDescriptor[] Capacity(
        string key, string fieldName, string defaultValue,
        Func<SystemSettingsUpdate, CapacityLimit?> read, Action<SystemSettingsDto, int?> write) =>
    [
        new CapacitySetting
        {
            Key = key,
            FieldName = fieldName,
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = defaultValue,
            CacheKeyToEvict = ImportExportLimitsLookup.CacheKey,
            Read = read,
            Write = write,
        },
    ];

    // All eight megabyte caps share one bound pair, so the factory carries it rather than taking it
    // eight times: SystemSettingsBounds declares a per-key pair for each of them, and every one is
    // 1-1024. The per-key constants are what AC 22(a) asserts against the [Range]; this is where they
    // are consumed. Passing the pair per call would be eight identical arguments and one more place
    // for a transcription slip.
    private static SystemSettingDescriptor[] Megabytes(
        string key, string fieldName, int defaultValue,
        Func<SystemSettingsUpdate, int?> read, Action<SystemSettingsDto, int> write) =>
    [
        new IntSetting
        {
            Key = key,
            FieldName = fieldName,
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = Int(defaultValue),
            CacheKeyToEvict = ImportExportLimitsLookup.CacheKey,
            Min = MegabytesMin,
            Max = MegabytesMax,
            Read = read,
            Write = write,
        },
    ];

    /// <summary>
    /// The shared megabyte bound pair. Named off one of the eight per-key constants rather than a
    /// literal, and asserted equal to all eight by <c>SystemSettingsBoundsTests</c>.
    /// </summary>
    private const int MegabytesMin = SystemSettingsBounds.ContactVCardMaxImportMegabytesMin;

    private const int MegabytesMax = SystemSettingsBounds.ContactVCardMaxImportMegabytesMax;
}
