using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos;

namespace Odyssey.Api.SystemSettings;

internal static partial class SystemSettingsRegistry
{
    private static SystemSettingDescriptor[] ContractsSummaryWindows() =>
    [
        // ── The Contracts summary windows ────────────────────────────────────────────────────────
        //
        // All three take the ORDINARY write claim, matching the analogous display bound above
        // (ContractMaxSummaryContracts):
        // the established split puts the authentication perimeter and the import/export SIZE caps
        // behind the stricter claim, and these are display bounds. The honest limit is that both
        // system-settings claims are Admin-only today, so the choice's only operational effect is the
        // derived AuditChanges — tracked as issue #438, which cannot ride along here because
        // decoupling AuditChanges from the claim would require weakening the very assertion AC 15
        // forbids weakening.
        //
        // Their own cache key rather than FinanceCapsCacheKey: one CacheKeyToEvict per descriptor, so
        // sharing would cross-evict the per-request caps. TouchOnPresenceOnly stays false — that is
        // reserved for the five original #349 keys.
        new IntSetting
        {
            Key = SystemSettingsKeys.ContractEndingWindowDays,
            Min = SystemSettingsBounds.ContractEndingWindowDaysMin,
            Max = SystemSettingsBounds.ContractEndingWindowDaysMax,
            FieldName = nameof(SystemSettingsUpdate.ContractEndingWindowDays),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.ContractEndingWindowDays),
            CacheKeyToEvict = SystemSettingsService.ContractSummaryCacheKey,
            Read = r => r.ContractEndingWindowDays,
            Write = (dto, v) => dto.ContractEndingWindowDays = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.ContractChargeWindowDays,
            Min = SystemSettingsBounds.ContractChargeWindowDaysMin,
            Max = SystemSettingsBounds.ContractChargeWindowDaysMax,
            FieldName = nameof(SystemSettingsUpdate.ContractChargeWindowDays),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.ContractChargeWindowDays),
            CacheKeyToEvict = SystemSettingsService.ContractSummaryCacheKey,
            Read = r => r.ContractChargeWindowDays,
            Write = (dto, v) => dto.ContractChargeWindowDays = v,
        },
        // The one of the three with a cost advisory. AboveDefault already covers PAYLOAD/RENDER cost,
        // not only memory, CPU and third-party spend — the rows are rendered blocks in an always-open
        // header region, so the cost of a raise is payload and render rather than query time.
        new IntSetting
        {
            Key = SystemSettingsKeys.ContractMaxSummaryCharges,
            Min = SystemSettingsBounds.ContractMaxSummaryChargesMin,
            Max = SystemSettingsBounds.ContractMaxSummaryChargesMax,
            FieldName = nameof(SystemSettingsUpdate.ContractMaxSummaryCharges),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.ContractMaxSummaryCharges),
            CacheKeyToEvict = SystemSettingsService.ContractSummaryCacheKey,
            Read = r => r.ContractMaxSummaryCharges,
            Write = (dto, v) => dto.ContractMaxSummaryCharges = v,
            Advise = SettingAdvisories.AboveDefault(
                dto => dto.ContractMaxSummaryCharges, SystemSettingsDefaults.ContractMaxSummaryCharges,
                "Each charge is rendered as its own row in the page-header panel."),
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
        // A cap: a degraded read resolves to min (issue #521 §4). Raising the max re-opens the series-delete copy bound
        // (RecurrenceMaxGeneratedOccurrencesMax × 2 × max ≤ 200,000 rows) — see SystemSettingsBounds.
        new IntSetting
        {
            Key = SystemSettingsKeys.CalendarEventMaxLinksPerKind,
            Min = SystemSettingsBounds.CalendarEventMaxLinksPerKindMin,
            Max = SystemSettingsBounds.CalendarEventMaxLinksPerKindMax,
            FieldName = nameof(SystemSettingsUpdate.CalendarEventMaxLinksPerKind),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.CalendarEventMaxLinksPerKind),
            CacheKeyToEvict = JournalLimitsLookup.CacheKey,
            Read = r => r.CalendarEventMaxLinksPerKind,
            Write = (dto, v) => dto.CalendarEventMaxLinksPerKind = v,
            Advise = SettingAdvisories.AboveDefault(
                dto => dto.CalendarEventMaxLinksPerKind, SystemSettingsDefaults.CalendarEventMaxLinksPerKind,
                "Deleting a recurring series copies its attachments onto every past occurrence it keeps, "
                + "so the copy grows with this value."),
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
    ];

    private static SystemSettingDescriptor[] TuningConstants() =>
    [
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
    ];
}
