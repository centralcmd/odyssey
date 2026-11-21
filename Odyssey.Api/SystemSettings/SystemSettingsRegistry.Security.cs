using Odyssey.Context;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos;

namespace Odyssey.Api.SystemSettings;

internal static partial class SystemSettingsRegistry
{
    private static SystemSettingDescriptor[] SecurityToggles() =>
    [
        // ── The three authentication-perimeter / security toggles (issue #349) ────────────────────
        // TouchOnPresenceOnly: the three original #349 perimeter toggles bump UpdatedAt on presence. See the
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
    ];

    private static SystemSettingDescriptor[] ImportExportCaps() =>
    [
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
            SystemSettingsDefaults.CalendarIcsMaxExportEvents,
            r => r.CalendarIcsMaxExportEvents, (dto, v) => dto.CalendarIcsMaxExportEvents = v),
        ..Capacity(SystemSettingsKeys.CalendarIcsMaxImportEvents, nameof(SystemSettingsUpdate.CalendarIcsMaxImportEvents),
            SystemSettingsDefaults.CalendarIcsMaxImportEvents,
            r => r.CalendarIcsMaxImportEvents, (dto, v) => dto.CalendarIcsMaxImportEvents = v),
        // The calendar import buffers its whole upload on the API's temp disk before it streams it (issue #516
        // §8.5), so a size above the old 1 GB ceiling says what that costs.
        ..Megabytes(SystemSettingsKeys.CalendarIcsMaxImportMegabytes, nameof(SystemSettingsUpdate.CalendarIcsMaxImportMegabytes),
            SystemSettingsDefaults.CalendarIcsMaxImportMegabytes,
            r => r.CalendarIcsMaxImportMegabytes, (dto, v) => dto.CalendarIcsMaxImportMegabytes = v,
            SystemSettingsBounds.CalendarIcsMaxImportMegabytesMin, SystemSettingsBounds.CalendarIcsMaxImportMegabytesMax,
            SettingAdvisories.CalendarIcsImportTempDisk(dto => dto.CalendarIcsMaxImportMegabytes)),
        ..Megabytes(SystemSettingsKeys.CalendarIcsMaxExportMegabytes, nameof(SystemSettingsUpdate.CalendarIcsMaxExportMegabytes),
            SystemSettingsDefaults.CalendarIcsMaxExportMegabytes,
            r => r.CalendarIcsMaxExportMegabytes, (dto, v) => dto.CalendarIcsMaxExportMegabytes = v,
            SystemSettingsBounds.CalendarIcsMaxExportMegabytesMin, SystemSettingsBounds.CalendarIcsMaxExportMegabytesMax,
            SettingAdvisories.CalendarIcsExportDuration(dto => dto.CalendarIcsMaxExportMegabytes)),

        ..Capacity(SystemSettingsKeys.TaskIcsMaxExportTasks, nameof(SystemSettingsUpdate.TaskIcsMaxExportTasks),
            SystemSettingsDefaults.TaskIcsMaxExportTasks,
            r => r.TaskIcsMaxExportTasks, (dto, v) => dto.TaskIcsMaxExportTasks = v),
        ..Capacity(SystemSettingsKeys.TaskIcsMaxImportTasks, nameof(SystemSettingsUpdate.TaskIcsMaxImportTasks),
            SystemSettingsDefaults.TaskIcsMaxImportTasks,
            r => r.TaskIcsMaxImportTasks, (dto, v) => dto.TaskIcsMaxImportTasks = v),
        ..Megabytes(SystemSettingsKeys.TaskIcsMaxImportMegabytes, nameof(SystemSettingsUpdate.TaskIcsMaxImportMegabytes),
            SystemSettingsDefaults.TaskIcsMaxImportMegabytes,
            r => r.TaskIcsMaxImportMegabytes, (dto, v) => dto.TaskIcsMaxImportMegabytes = v,
            SystemSettingsBounds.TaskIcsMaxImportMegabytesMin, SystemSettingsBounds.TaskIcsMaxImportMegabytesMax),
        ..Megabytes(SystemSettingsKeys.TaskIcsMaxExportMegabytes, nameof(SystemSettingsUpdate.TaskIcsMaxExportMegabytes),
            SystemSettingsDefaults.TaskIcsMaxExportMegabytes,
            r => r.TaskIcsMaxExportMegabytes, (dto, v) => dto.TaskIcsMaxExportMegabytes = v,
            SystemSettingsBounds.TaskIcsMaxExportMegabytesMin, SystemSettingsBounds.TaskIcsMaxExportMegabytesMax),

        ..Capacity(SystemSettingsKeys.JournalIcsMaxExportRows, nameof(SystemSettingsUpdate.JournalIcsMaxExportRows),
            SystemSettingsDefaults.JournalIcsMaxExportRows,
            r => r.JournalIcsMaxExportRows, (dto, v) => dto.JournalIcsMaxExportRows = v),
        ..Capacity(SystemSettingsKeys.JournalIcsMaxImportEntries, nameof(SystemSettingsUpdate.JournalIcsMaxImportEntries),
            SystemSettingsDefaults.JournalIcsMaxImportEntries,
            r => r.JournalIcsMaxImportEntries, (dto, v) => dto.JournalIcsMaxImportEntries = v),
        ..Megabytes(SystemSettingsKeys.JournalIcsMaxImportMegabytes, nameof(SystemSettingsUpdate.JournalIcsMaxImportMegabytes),
            SystemSettingsDefaults.JournalIcsMaxImportMegabytes,
            r => r.JournalIcsMaxImportMegabytes, (dto, v) => dto.JournalIcsMaxImportMegabytes = v,
            SystemSettingsBounds.JournalIcsMaxImportMegabytesMin, SystemSettingsBounds.JournalIcsMaxImportMegabytesMax),
        ..Megabytes(SystemSettingsKeys.JournalIcsMaxExportMegabytes, nameof(SystemSettingsUpdate.JournalIcsMaxExportMegabytes),
            SystemSettingsDefaults.JournalIcsMaxExportMegabytes,
            r => r.JournalIcsMaxExportMegabytes, (dto, v) => dto.JournalIcsMaxExportMegabytes = v,
            SystemSettingsBounds.JournalIcsMaxExportMegabytesMin, SystemSettingsBounds.JournalIcsMaxExportMegabytesMax),
    ];

    private static SystemSettingDescriptor[] TransactionalEmail() =>
    [
        // ── Transactional email (issue #421 Wave 2, extended by issue #8) ─────────────────────────
        // Every one takes the STRICTER claim. The sender identity is what recipients see and what the
        // relay authorises; the throttle is a security control on the anonymous mail path; and the
        // four transport keys decide where mail goes, whether it is encrypted, and what address every
        // reset link points at.
        //
        // The transport four were absent by design until issue #8 (issue #421 Non-Goal 2): a writable
        // host harvests the relay credential and every reset token, because the sender connects and
        // THEN authenticates. That threat is now closed STRUCTURALLY rather than by detection — see
        // SystemSettingsService.UpdateAsync, which clears the stored credential in the same
        // transaction as the host change. EmailClientBaseUrl has no such structural control and its
        // residual risk is stated and accepted in issue #8 §10.2.
        //
        // Ordering: the transport rows come FIRST, matching the design system's Email group. They
        // frame everything below them — whether mail leaves at all, where it goes, and whether the
        // credential beneath them travels encrypted.
        new StringSetting
        {
            Key = SystemSettingsKeys.EmailSmtpHost,
            FieldName = nameof(SystemSettingsUpdate.EmailSmtpHost),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = SystemSettingsDefaults.EmailSmtpHost,
            MaxLength = EmailSmtpHostRule.MaxLength,
            // Empty is a legal, meaningful value here — "mail is not configured" — and is the only
            // route back to it, since null already means "leave unchanged".
            AllowEmpty = true,
            Validator = EmailSmtpHostRule.Validate,
            Canonicalize = EmailSmtpHostRule.Canonicalize,
            // The audit line echoes the value being REPLACED, which the write validator never saw: a
            // row planted by a restore can carry `user:pass@host`. Both ends go through the
            // projection so the change stays reconstructable without the line carrying a credential.
            AuditProjection = EmailSmtpHostRule.Host,
            ReadValidator = EmailSmtpHostRule.Validate,
            Read = r => r.EmailSmtpHost,
            Write = (dto, v) => dto.EmailSmtpHost = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.EmailSmtpPort,
            Min = SystemSettingsBounds.EmailSmtpPortMin,
            Max = SystemSettingsBounds.EmailSmtpPortMax,
            FieldName = nameof(SystemSettingsUpdate.EmailSmtpPort),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = Int(SystemSettingsDefaults.EmailSmtpPort),
            Read = r => r.EmailSmtpPort,
            Write = (dto, v) => dto.EmailSmtpPort = v,
        },
        new BoolSetting
        {
            Key = SystemSettingsKeys.EmailUseStartTls,
            FieldName = nameof(SystemSettingsUpdate.EmailUseStartTls),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = Bool(SystemSettingsDefaults.EmailUseStartTls),
            Read = r => r.EmailUseStartTls,
            Write = (dto, v) => dto.EmailUseStartTls = v,
        },
        // The one field in this group that G4/G7 do not protect, because no credential is involved:
        // whoever changes it receives a password-reset token for any address they know, including
        // another administrator's. Hence the claim, the host-only audit projection, the https-only
        // rule and — the only control that narrows this at no cost to legitimate use — the
        // client-side origin-mismatch hint (which is client-side because AdvisoryContext carries no
        // HttpContext and Advise runs on every GET, not after a save).
        new StringSetting
        {
            Key = SystemSettingsKeys.EmailClientBaseUrl,
            FieldName = nameof(SystemSettingsUpdate.EmailClientBaseUrl),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = SystemSettingsDefaults.EmailClientBaseUrl,
            MaxLength = EmailClientBaseUrlRule.MaxLength,
            AllowEmpty = true,
            Validator = EmailClientBaseUrlRule.Validate,
            Canonicalize = EmailClientBaseUrlRule.Canonicalize,
            AuditProjection = EmailClientBaseUrlRule.Host,
            // The read-path validator issue #8 §5.9 added StringSetting.ReadValidator for. Without it
            // an http:// public host planted by a restore fails closed on send while the settings
            // page shows the row as healthy — invisible, on the weakest field in the feature.
            ReadValidator = EmailClientBaseUrlRule.Validate,
            Read = r => r.EmailClientBaseUrl,
            Write = (dto, v) => dto.EmailClientBaseUrl = v,
        },
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
    ];

    private static SystemSettingDescriptor[] RequestCaps() =>
    [
        // ── Per-request defensive caps (issue #421 Wave 3) ────────────────────────────────────────
        // All nine take the ORDINARY write claim. They are availability bounds, so appsec argued for
        // the stricter one; the reasoning for keeping them here is in issue #421 §10.10 — each is
        // bounded by [Range(1, 100000)], the two with real leverage are additionally ceilinged by
        // their DTO annotations, and every holder of either claim is Admin today, so the split is
        // defence-in-depth against a future role rather than a live boundary.
        //
        // Three cache keys, not one: the caps span two domain projects, and a lookup interface lives
        // in the project that consumes it so that project's tests can fake it. Contracts are
        // Odyssey.Core.Finance; photos and journal are Odyssey.Core.Journal.
        //
        // Every cap below evicts FinanceCapsCacheKey, because all of them are served off
        // FinanceRequestCaps. ContractSummaryCacheKey holds a DIFFERENT set — the two windows and the
        // charge-row cap — so evicting it here would leave the changed cap stale for the cache TTL
        // while needlessly dropping rows the change never touched (issue #28).
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
            Key = SystemSettingsKeys.ContractMaxTermsPerContract,
            Min = SystemSettingsBounds.ContractMaxTermsPerContractMin,
            Max = SystemSettingsBounds.ContractMaxTermsPerContractMax,
            FieldName = nameof(SystemSettingsUpdate.ContractMaxTermsPerContract),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.ContractMaxTermsPerContract),
            CacheKeyToEvict = SystemSettingsService.FinanceCapsCacheKey,
            Read = r => r.ContractMaxTermsPerContract,
            Write = (dto, v) => dto.ContractMaxTermsPerContract = v,
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
    ];
}
