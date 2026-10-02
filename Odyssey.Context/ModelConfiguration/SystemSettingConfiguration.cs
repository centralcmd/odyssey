using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Odyssey.Dtos;

namespace Odyssey.Context.ModelConfiguration;

internal sealed class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> entity)
    {
        // Migration-seeded singleton-per-key rows (issue #349): Key is the primary key (a natural
        // key), so seeding is a plain HasData insert, not the positional-id hand-written-migration
        // dance the permission-claim seeds once needed. GET assembles the DTO from these five rows and
        // never writes, so a fixed UpdatedAt/null UpdatedBy at seed time is the correct "nobody has
        // touched this yet" starting state.
        var seededAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        entity.HasData(
            new SystemSetting { Key = SystemSettingsKeys.RequireTwoFactor, Value = "false", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.RegistrationRequireAdminApproval, Value = "true", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.EmailRequireConfirmation, Value = "true", UpdatedAt = seededAt },
            // Import/export volume caps (issue #343 §6/§15) — seeded to today's effective values so
            // out-of-the-box behavior is unchanged. The two vCard count caps seed "unlimited" (today's
            // effective int.MaxValue); the three ICS surfaces keep their existing 2,000-derived count
            // defaults. All eight size (MB) fields below are seeded at 64, a later unification of what
            // was originally a per-surface split (see SystemSettingsKeys' doc comment).
            new SystemSetting { Key = SystemSettingsKeys.ContactVCardMaxExportRows, Value = SystemSettingsDefaults.ContactVCardMaxExportRows, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.ContactVCardMaxImportEntries, Value = SystemSettingsDefaults.ContactVCardMaxImportEntries, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.ContactVCardMaxImportMegabytes, Value = "64", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarIcsMaxExportEvents, Value = "2000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarIcsMaxImportEvents, Value = "2000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarIcsMaxImportMegabytes, Value = "64", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.TaskIcsMaxImportTasks, Value = "2000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.TaskIcsMaxImportMegabytes, Value = "64", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.JournalIcsMaxExportRows, Value = "2000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.JournalIcsMaxImportEntries, Value = "2000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.JournalIcsMaxImportMegabytes, Value = "64", UpdatedAt = seededAt },
            // Export-side follow-up (post-#343): a "maximum export file size" per surface, plus a Tasks
            // export row cap (Tasks previously had none — see SystemSettingsKeys' doc comment).
            new SystemSetting { Key = SystemSettingsKeys.ContactVCardMaxExportMegabytes, Value = "64", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarIcsMaxExportMegabytes, Value = "64", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.TaskIcsMaxExportTasks, Value = "2000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.TaskIcsMaxExportMegabytes, Value = "64", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.JournalIcsMaxExportMegabytes, Value = "64", UpdatedAt = seededAt },
            // AI file-analysis policy and processor disclosure (issue #421 Wave 1). Values mirror
            // today's effective behaviour, with one correction: MaxFutureTransactionDays was 90 in
            // appsettings.json and 30 on FileAnalysisOptions, and 90 is what ran.
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisProcessor, Value = SystemSettingsDefaults.FileAnalysisProcessor, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisProcessorRegion, Value = SystemSettingsDefaults.FileAnalysisProcessorRegion, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisLawfulBasis, Value = SystemSettingsDefaults.FileAnalysisLawfulBasis, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisPrivacyNoticeUrl, Value = SystemSettingsDefaults.FileAnalysisPrivacyNoticeUrl, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisMaxFutureTransactionDays, Value = "90", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisMatchAutoLinkThreshold, Value = "0.6", UpdatedAt = seededAt },
            // The SMTP transport and the public link origin (issue #8). Seeded EMPTY for the two
            // string keys, and that empty value is the real one: there is no configuration to adopt
            // from and no environment fallback, so a fresh deployment starts with mail switched off
            // until an administrator sets a relay at /settings. Documented in docs/deployment.md,
            // because the consequence is that the forgot-password flow cannot recover the bootstrap
            // administrator during that window.
            //
            // UpdatedBy is left null by the seed — the provenance line on the settings page reads it
            // as "nobody has taken ownership of this row yet", which is exactly true here.
            new SystemSetting { Key = SystemSettingsKeys.EmailSmtpHost, Value = SystemSettingsDefaults.EmailSmtpHost, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.EmailSmtpPort, Value = "587", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.EmailUseStartTls, Value = "true", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.EmailClientBaseUrl, Value = SystemSettingsDefaults.EmailClientBaseUrl, UpdatedAt = seededAt },
            // Transactional email (issue #421 Wave 2). The sender identity is set at /settings; the
            // seeded default is deliberately unusable (odyssey.local), so a deployment that never sets
            // it fails visibly at the relay rather than sending as a plausible-looking wrong address.
            new SystemSetting { Key = SystemSettingsKeys.EmailFromAddress, Value = SystemSettingsDefaults.EmailFromAddress, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.EmailFromName, Value = SystemSettingsDefaults.EmailFromName, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.EmailPerRecipientLimit, Value = "3", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.EmailPerRecipientWindowMinutes, Value = "60", UpdatedAt = seededAt },
            // Per-request defensive caps (issue #421 Wave 3). These had no appsettings entry and no
            // environment plumbing at all — they were POCO defaults and two `private const`s — so no
            // config-adoption entry is needed: there was never a configured value to carry over.
            new SystemSetting { Key = SystemSettingsKeys.ContractMaxPartiesPerContract, Value = "25", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.ContractMaxFilesPerContract, Value = "50", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.ContractMaxTermsPerContract, Value = "500", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.ContractMaxSummaryContracts, Value = "1000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.PhotoMaxLinksPerKind, Value = "50", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.PhotoMaxAlbumMembers, Value = "1000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.JournalEntryMaxLinksPerKind, Value = "50", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.JournalTaskMaxLinksPerKind, Value = "50", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileStorageMaxUploadMegabytes, Value = "64", UpdatedAt = seededAt },
            // The last compiled-in tuning constants (issue #434). Every seed is today's effective
            // value, so a default install is behaviourally identical after the migration — with the
            // single deliberate exception of the two Wave 3 ICS link caps, which start being honoured
            // on the import path where a hardcoded 50 used to win.
            //
            // Only the three FileAnalysis keys ever had a documented configuration surface; ten were
            // `const` and two were POCO defaults on a section with no appsettings entry. None of them
            // has one now — an administrator sets every one of these at /settings.
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisMaxTokens, Value = "8096", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisMatchMaxVocabulary, Value = "500", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisMatchTimeoutSeconds, Value = "60", UpdatedAt = seededAt },
            // Bytes on the options class, MEGABYTES here — matching the nine existing size settings.
            new SystemSetting { Key = SystemSettingsKeys.PhotoMetadataReadMegabytes, Value = "8", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.PhotoMetadataExtractionTimeoutSeconds, Value = "5", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarMaxWindowDays, Value = "92", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarMaxEventDurationDays, Value = "366", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarIcsMaxAggregateExportRows, Value = "20000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarIcsMaxAggregateOccurrences, Value = "5000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.CalendarIcsMaxAggregateExportWindowDays, Value = "92", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.RecurrenceMaxGeneratedOccurrences, Value = "1000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.ContactVCardMaxRepeatablePropertiesPerEntry, Value = "200", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.ImportMaxSamplesPerSkipReason, Value = "100", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.EmailMaxTrackedRecipients, Value = "20000", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.AccountMaxSmartTagsPerAccount, Value = "20", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.ContractMaxSmartTagsPerContract, Value = "20", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.PropertyMaxSmartTagsPerProperty, Value = "20", UpdatedAt = seededAt },
            // The file-analysis kill switch, model and destination (issue #439). Seeded to today's
            // effective values, so a default install is behaviourally identical: analysis OFF,
            // claude-sonnet-5, api.anthropic.com.
            //
            // UpdatedBy is left null by the seed, meaning no administrator has taken ownership of the
            // row — which is what the settings page's provenance line reads. There is no environment
            // variable behind any of the three: InsertData is a compile-time constant, and nothing
            // carries a configured value into the store.
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisEnabled, Value = "false", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisModel, Value = SystemSettingsDefaults.FileAnalysisModel, UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.FileAnalysisBaseUrl, Value = SystemSettingsDefaults.FileAnalysisBaseUrl, UpdatedAt = seededAt },
            // The Contracts summary windows. The ending-soon window seeds the client `const 45` it
            // replaces exactly, so a default install is behaviourally identical; the other two are new
            // and seed the design system's own defaults (a 45-day look-ahead, six rendered rows).
            new SystemSetting { Key = SystemSettingsKeys.ContractEndingWindowDays, Value = "45", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.ContractChargeWindowDays, Value = "45", UpdatedAt = seededAt },
            new SystemSetting { Key = SystemSettingsKeys.ContractMaxSummaryCharges, Value = "6", UpdatedAt = seededAt }
        );
    }
}
