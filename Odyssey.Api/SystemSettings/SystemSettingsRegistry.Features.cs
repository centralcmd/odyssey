using Odyssey.Core.Finance;
using Odyssey.Context;
using Odyssey.Core.SystemSettings;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;

namespace Odyssey.Api.SystemSettings;

internal static partial class SystemSettingsRegistry
{
    private static SystemSettingDescriptor[] FileAnalysisPolicy() =>
    [
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
            Advise = SettingAdvisories.DisclosureChangeRefusesOpenMatches,
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
            Advise = SettingAdvisories.DisclosureChangeRefusesOpenMatches,
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
            Advise = SettingAdvisories.DisclosureChangeRefusesOpenMatches,
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
    ];

    private static SystemSettingDescriptor[] FileAnalysisKillSwitch() =>
    [
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
    ];

    private static SystemSettingDescriptor[] PromptGuidance() =>
    [
        // ── Administrator prompt guidance (issue #433) ───────────────────────────────────────────
        //
        // The ORDINARY claim, a product-owner decision. AuditChanges is therefore false for both; the
        // digest audit line (AuditDigestOnChange) records who changed what, without the text.
        new StringSetting
        {
            Key = SystemSettingsKeys.FileAnalysisExtractionPromptAddition,
            FieldName = nameof(SystemSettingsUpdate.FileAnalysisExtractionPromptAddition),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = SystemSettingsDefaults.FileAnalysisExtractionPromptAddition,
            CacheKeyToEvict = FileAnalysisSettingsLookup.CacheKey,
            MaxLength = PromptAdditionRule.MaxLength,
            Multiline = true,
            AllowEmpty = true,
            AuditDigestOnChange = true,
            ReadValidator = PromptAdditionRule.Validate,
            Read = r => r.FileAnalysisExtractionPromptAddition,
            Write = (dto, v) => dto.FileAnalysisExtractionPromptAddition = v,
            Advise = SettingAdvisories.GuidanceSet(dto => dto.FileAnalysisExtractionPromptAddition, refusesOpenMatches: false),
        },
        new StringSetting
        {
            Key = SystemSettingsKeys.FileAnalysisMatchPromptAddition,
            FieldName = nameof(SystemSettingsUpdate.FileAnalysisMatchPromptAddition),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = SystemSettingsDefaults.FileAnalysisMatchPromptAddition,
            CacheKeyToEvict = FileAnalysisSettingsLookup.CacheKey,
            MaxLength = PromptAdditionRule.MaxLength,
            Multiline = true,
            AllowEmpty = true,
            AuditDigestOnChange = true,
            ReadValidator = PromptAdditionRule.Validate,
            Read = r => r.FileAnalysisMatchPromptAddition,
            Write = (dto, v) => dto.FileAnalysisMatchPromptAddition = v,
            Advise = SettingAdvisories.GuidanceSet(dto => dto.FileAnalysisMatchPromptAddition, refusesOpenMatches: true),
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
    ];

    private static SystemSettingDescriptor[] ContractSmartTags() =>
    [
        // ── Contracts: the smart-tag cap (issue #166) ─────────────────────────────────────────────
        //
        // The ORDINARY write claim, matching its account sibling and the other display-bound keys.
        // Its own cache key, not AccountLimitsLookup's: SystemSettingDescriptor.CacheKeyToEvict is a
        // single string, so a shared entry would make a contract change evict the account limits and
        // vice versa.
        new IntSetting
        {
            Key = SystemSettingsKeys.ContractMaxSmartTagsPerContract,
            Min = SystemSettingsBounds.ContractMaxSmartTagsPerContractMin,
            Max = SystemSettingsBounds.ContractMaxSmartTagsPerContractMax,
            FieldName = nameof(SystemSettingsUpdate.ContractMaxSmartTagsPerContract),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.ContractMaxSmartTagsPerContract),
            CacheKeyToEvict = ContractLimitsLookup.CacheKey,
            Read = r => r.ContractMaxSmartTagsPerContract,
            Write = (dto, v) => dto.ContractMaxSmartTagsPerContract = v,
        },
    ];

    private static SystemSettingDescriptor[] PropertySmartTags() =>
    [
        // ── Properties: the smart-tag cap (issue #167) ────────────────────────────────────────────
        //
        // The ORDINARY write claim, matching its account and contract siblings, and its own cache key
        // for the reason the contract entry gives.
        new IntSetting
        {
            Key = SystemSettingsKeys.PropertyMaxSmartTagsPerProperty,
            Min = SystemSettingsBounds.PropertyMaxSmartTagsPerPropertyMin,
            Max = SystemSettingsBounds.PropertyMaxSmartTagsPerPropertyMax,
            FieldName = nameof(SystemSettingsUpdate.PropertyMaxSmartTagsPerProperty),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.PropertyMaxSmartTagsPerProperty),
            CacheKeyToEvict = PropertyLimitsLookup.CacheKey,
            Read = r => r.PropertyMaxSmartTagsPerProperty,
            Write = (dto, v) => dto.PropertyMaxSmartTagsPerProperty = v,
        },
    ];

    private static SystemSettingDescriptor[] Weather() =>
    [
        // ── Weather (issue #348 §11) ──────────────────────────────────────────────────────────────
        //
        // The ordinary write claim on all four, and one cache key: the scheduler and the read path read
        // them together through WeatherSettingsLookup. The contact is an email or https URL sent in the
        // User-Agent, so its shape check keeps CR/LF out of a header; the fetch path ALSO fails closed on
        // an unusable stored value (ReadValidator), so a row planted by a restore cannot reach MET either.
        new StringSetting
        {
            Key = SystemSettingsKeys.WeatherUserAgentContact,
            FieldName = nameof(SystemSettingsUpdate.WeatherUserAgentContact),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = SystemSettingsDefaults.WeatherUserAgentContact,
            CacheKeyToEvict = WeatherSettingsLookup.CacheKey,
            MaxLength = WeatherRules.ContactMaxLength,
            AllowEmpty = true,
            Validator = WeatherRules.ValidateContact,
            ReadValidator = WeatherRules.ValidateContact,
            Read = r => r.WeatherUserAgentContact,
            Write = (dto, v) => dto.WeatherUserAgentContact = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.WeatherRefreshIntervalMinutes,
            Min = SystemSettingsBounds.WeatherRefreshIntervalMinutesMin,
            Max = SystemSettingsBounds.WeatherRefreshIntervalMinutesMax,
            FieldName = nameof(SystemSettingsUpdate.WeatherRefreshIntervalMinutes),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.WeatherRefreshIntervalMinutes),
            CacheKeyToEvict = WeatherSettingsLookup.CacheKey,
            Read = r => r.WeatherRefreshIntervalMinutes,
            Write = (dto, v) => dto.WeatherRefreshIntervalMinutes = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.WeatherMaxTrackedPoints,
            Min = SystemSettingsBounds.WeatherMaxTrackedPointsMin,
            Max = SystemSettingsBounds.WeatherMaxTrackedPointsMax,
            FieldName = nameof(SystemSettingsUpdate.WeatherMaxTrackedPoints),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.WeatherMaxTrackedPoints),
            CacheKeyToEvict = WeatherSettingsLookup.CacheKey,
            Read = r => r.WeatherMaxTrackedPoints,
            Write = (dto, v) => dto.WeatherMaxTrackedPoints = v,
        },
        // GUID shape only here. Selectability (a point with forecast on, not purging) is a question about
        // other tables, so SystemSettingsService asks IWeatherPointRules inside the write's
        // transaction — the one key that takes that path, which a guard test pins.
        new StringSetting
        {
            Key = SystemSettingsKeys.WeatherDefaultPointId,
            FieldName = nameof(SystemSettingsUpdate.WeatherDefaultPointId),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = SystemSettingsDefaults.WeatherDefaultPointId,
            CacheKeyToEvict = WeatherSettingsLookup.CacheKey,
            MaxLength = 36,
            AllowEmpty = true,
            Validator = WeatherRules.ValidateDefaultPointId,
            ReadValidator = WeatherRules.ValidateDefaultPointId,
            Canonicalize = v => Guid.TryParse(v, out var id) ? id.ToString("D") : v,
            Read = r => r.WeatherDefaultPointId,
            Write = (dto, v) => dto.WeatherDefaultPointId = v,
        },
    ];

    private static SystemSettingDescriptor[] ExchangeRateSync() =>
    [
        // ── Exchange-rate sync (issue #349 §8) ────────────────────────────────────────────────────
        //
        // The ORDINARY write claim: neither is a credential or a destination. No cache key — the
        // worker's sync job reads both live on each tick, so there is no snapshot to evict.
        new StringSetting
        {
            Key = SystemSettingsKeys.ExchangeRateHistoryStartDate,
            FieldName = nameof(SystemSettingsUpdate.ExchangeRateHistoryStartDate),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = SystemSettingsDefaults.ExchangeRateHistoryStartDate,
            MaxLength = ExchangeRateHistoryStartDateRule.MaxLength,
            // Empty means "1 January of the current UTC year" and is the only way back to it.
            AllowEmpty = true,
            // Format and floor only. "On or before today" needs a clock and is checked in
            // SystemSettingsService against the injected TimeProvider.
            Validator = ExchangeRateHistoryStartDateRule.Validate,
            ReadValidator = ExchangeRateHistoryStartDateRule.Validate,
            Read = r => r.ExchangeRateHistoryStartDate,
            Write = (dto, v) => dto.ExchangeRateHistoryStartDate = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.ExchangeRateSyncIntervalHours,
            Min = SystemSettingsBounds.ExchangeRateSyncIntervalHoursMin,
            Max = SystemSettingsBounds.ExchangeRateSyncIntervalHoursMax,
            FieldName = nameof(SystemSettingsUpdate.ExchangeRateSyncIntervalHours),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.ExchangeRateSyncIntervalHours),
            Read = r => r.ExchangeRateSyncIntervalHours,
            Write = (dto, v) => dto.ExchangeRateSyncIntervalHours = v,
            Advise = SettingAdvisories.SyncIntervalBelowPublication,
        },
    ];

    private static SystemSettingDescriptor[] InstrumentPrices() =>
    [
        // ── Instrument prices (issue #446 §8.4) ──────────────────────────────────────────────────
        //
        // No cache key: the API reads the stale window through InstrumentPriceSettingsLookup, which reads live
        // and caches only its watermark, like the exchange-rate sync's lookup.
        new IntSetting
        {
            Key = SystemSettingsKeys.InstrumentPriceStaleAfterDays,
            Min = SystemSettingsBounds.InstrumentPriceStaleAfterDaysMin,
            Max = SystemSettingsBounds.InstrumentPriceStaleAfterDaysMax,
            FieldName = nameof(SystemSettingsUpdate.InstrumentPriceStaleAfterDays),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.InstrumentPriceStaleAfterDays),
            Read = r => r.InstrumentPriceStaleAfterDays,
            Write = (dto, v) => dto.InstrumentPriceStaleAfterDays = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.InstrumentPriceSyncIntervalHours,
            Min = SystemSettingsBounds.InstrumentPriceSyncIntervalHoursMin,
            Max = SystemSettingsBounds.InstrumentPriceSyncIntervalHoursMax,
            FieldName = nameof(SystemSettingsUpdate.InstrumentPriceSyncIntervalHours),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.InstrumentPriceSyncIntervalHours),
            Read = r => r.InstrumentPriceSyncIntervalHours,
            Write = (dto, v) => dto.InstrumentPriceSyncIntervalHours = v,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.InstrumentPriceSyncHistoryDays,
            Min = SystemSettingsBounds.InstrumentPriceSyncHistoryDaysMin,
            Max = SystemSettingsBounds.InstrumentPriceSyncHistoryDaysMax,
            FieldName = nameof(SystemSettingsUpdate.InstrumentPriceSyncHistoryDays),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.InstrumentPriceSyncHistoryDays),
            Read = r => r.InstrumentPriceSyncHistoryDays,
            Write = (dto, v) => dto.InstrumentPriceSyncHistoryDays = v,
            Advise = SettingAdvisories.InstrumentHistoryCostsQuota,
        },
    ];

    private static SystemSettingDescriptor[] NewsReader() =>
    [
        // ── News reader (issue #470 §11.1) ───────────────────────────────────────────────────────
        //
        // Eight ordinary keys and the allow-list, which takes the SECURITY claim: it decides which private addresses
        // the worker may fetch from, so every change is audited. The eight ints are served by the news settings lookup
        // on the shared TTL and evict its entry; the allow-list is read live and uncached (§7.9), so it evicts nothing
        // — a save reaches this host's next fetch at once, the worker's after its own TTL. The image size cap also has a
        // derived ceiling — FileStorage:MaxFileSizeBytes, through NewsImageSizeCeiling — which cannot live in an
        // attribute, so it is the one key here with a Validator.
        new IntSetting
        {
            Key = SystemSettingsKeys.NewsDefaultFetchIntervalMinutes,
            Min = SystemSettingsBounds.NewsDefaultFetchIntervalMinutesMin,
            Max = SystemSettingsBounds.NewsDefaultFetchIntervalMinutesMax,
            FieldName = nameof(SystemSettingsUpdate.NewsDefaultFetchIntervalMinutes),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.NewsDefaultFetchIntervalMinutes),
            Read = r => r.NewsDefaultFetchIntervalMinutes,
            Write = (dto, v) => dto.NewsDefaultFetchIntervalMinutes = v,
            CacheKeyToEvict = Odyssey.Core.Internet.News.NewsSettingsLookup.CacheKey,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.NewsMaxFeeds,
            Min = SystemSettingsBounds.NewsMaxFeedsMin,
            Max = SystemSettingsBounds.NewsMaxFeedsMax,
            FieldName = nameof(SystemSettingsUpdate.NewsMaxFeeds),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.NewsMaxFeeds),
            Read = r => r.NewsMaxFeeds,
            Write = (dto, v) => dto.NewsMaxFeeds = v,
            CacheKeyToEvict = Odyssey.Core.Internet.News.NewsSettingsLookup.CacheKey,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.NewsImageMaxPerArticle,
            Min = SystemSettingsBounds.NewsImageMaxPerArticleMin,
            Max = SystemSettingsBounds.NewsImageMaxPerArticleMax,
            FieldName = nameof(SystemSettingsUpdate.NewsImageMaxPerArticle),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.NewsImageMaxPerArticle),
            Read = r => r.NewsImageMaxPerArticle,
            Write = (dto, v) => dto.NewsImageMaxPerArticle = v,
            CacheKeyToEvict = Odyssey.Core.Internet.News.NewsSettingsLookup.CacheKey,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.NewsImageMaxSizeKb,
            Min = SystemSettingsBounds.NewsImageMaxSizeKbMin,
            Max = SystemSettingsBounds.NewsImageMaxSizeKbMax,
            FieldName = nameof(SystemSettingsUpdate.NewsImageMaxSizeKb),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = Int(SystemSettingsDefaults.NewsImageMaxSizeKb),
            Validator = (value, ceilings) => ceilings.ValidateNewsImageMaxSizeKb(value),
            Read = r => r.NewsImageMaxSizeKb,
            Write = (dto, v) => dto.NewsImageMaxSizeKb = v,
            CacheKeyToEvict = Odyssey.Core.Internet.News.NewsSettingsLookup.CacheKey,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.NewsImageStoreMaxGiB,
            Min = SystemSettingsBounds.NewsImageStoreMaxGiBMin,
            Max = SystemSettingsBounds.NewsImageStoreMaxGiBMax,
            FieldName = nameof(SystemSettingsUpdate.NewsImageStoreMaxGiB),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.NewsImageStoreMaxGiB),
            Read = r => r.NewsImageStoreMaxGiB,
            Write = (dto, v) => dto.NewsImageStoreMaxGiB = v,
            CacheKeyToEvict = Odyssey.Core.Internet.News.NewsSettingsLookup.CacheKey,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.NewsTextStoreMaxGiB,
            Min = SystemSettingsBounds.NewsTextStoreMaxGiBMin,
            Max = SystemSettingsBounds.NewsTextStoreMaxGiBMax,
            FieldName = nameof(SystemSettingsUpdate.NewsTextStoreMaxGiB),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.NewsTextStoreMaxGiB),
            Read = r => r.NewsTextStoreMaxGiB,
            Write = (dto, v) => dto.NewsTextStoreMaxGiB = v,
            CacheKeyToEvict = Odyssey.Core.Internet.News.NewsSettingsLookup.CacheKey,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.NewsFeedMaxArticlesPerDay,
            Min = SystemSettingsBounds.NewsFeedMaxArticlesPerDayMin,
            Max = SystemSettingsBounds.NewsFeedMaxArticlesPerDayMax,
            FieldName = nameof(SystemSettingsUpdate.NewsFeedMaxArticlesPerDay),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.NewsFeedMaxArticlesPerDay),
            Read = r => r.NewsFeedMaxArticlesPerDay,
            Write = (dto, v) => dto.NewsFeedMaxArticlesPerDay = v,
            CacheKeyToEvict = Odyssey.Core.Internet.News.NewsSettingsLookup.CacheKey,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.NewsFeedMaxImageMbPerDay,
            Min = SystemSettingsBounds.NewsFeedMaxImageMbPerDayMin,
            Max = SystemSettingsBounds.NewsFeedMaxImageMbPerDayMax,
            FieldName = nameof(SystemSettingsUpdate.NewsFeedMaxImageMbPerDay),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.NewsFeedMaxImageMbPerDay),
            Read = r => r.NewsFeedMaxImageMbPerDay,
            Write = (dto, v) => dto.NewsFeedMaxImageMbPerDay = v,
            CacheKeyToEvict = Odyssey.Core.Internet.News.NewsSettingsLookup.CacheKey,
        },
        new StringSetting
        {
            Key = SystemSettingsKeys.NewsPrivateHostAllowList,
            FieldName = nameof(SystemSettingsUpdate.NewsPrivateHostAllowList),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = SystemSettingsDefaults.NewsPrivateHostAllowList,
            MaxLength = NewsPrivateHostAllowListRule.MaxLength,
            // Empty is the shipped value and the only route back to "no private host": null means "leave unchanged".
            AllowEmpty = true,
            Validator = NewsPrivateHostAllowListRule.Validate,
            Canonicalize = NewsPrivateHostAllowListRule.Canonicalize,
            // A row planted by a restore is reported, not rendered as healthy; the worker reads it as empty.
            ReadValidator = NewsPrivateHostAllowListRule.Validate,
            Read = r => r.NewsPrivateHostAllowList,
            Write = (dto, v) => dto.NewsPrivateHostAllowList = v,
        },
    ];

    private static SystemSettingDescriptor[] NewsOpmlImport() =>
    [
        // ── News OPML import (issue #508 §8.5) ───────────────────────────────────────────────────
        // The size cap follows the other import size caps (security claim, the import/export limits cache the
        // size middleware reads); the entry cap follows the count caps and is read by the news settings lookup.
        ..Megabytes(SystemSettingsKeys.NewsOpmlMaxImportMegabytes, nameof(SystemSettingsUpdate.NewsOpmlMaxImportMegabytes),
            SystemSettingsDefaults.NewsOpmlMaxImportMegabytes,
            r => r.NewsOpmlMaxImportMegabytes, (dto, v) => dto.NewsOpmlMaxImportMegabytes = v),
        new IntSetting
        {
            Key = SystemSettingsKeys.NewsOpmlMaxImportEntries,
            Min = SystemSettingsBounds.NewsOpmlMaxImportEntriesMin,
            Max = SystemSettingsBounds.NewsOpmlMaxImportEntriesMax,
            FieldName = nameof(SystemSettingsUpdate.NewsOpmlMaxImportEntries),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.NewsOpmlMaxImportEntries),
            Read = r => r.NewsOpmlMaxImportEntries,
            Write = (dto, v) => dto.NewsOpmlMaxImportEntries = v,
            CacheKeyToEvict = Odyssey.Core.Internet.News.NewsSettingsLookup.CacheKey,
        },
    ];

    private static SystemSettingDescriptor[] Podcasts() =>
    [
        // ── Podcasts (issue #556 §8.2) ───────────────────────────────────────────────────────────
        // All four are caps read by the news settings lookup (degraded read → min). The episode cap is also bounded by
        // the derived file-size ceiling, which cannot live in an attribute because it comes from startup configuration.
        new IntSetting
        {
            Key = SystemSettingsKeys.PodcastAudioStoreMaxGiB,
            Min = SystemSettingsBounds.PodcastAudioStoreMaxGiBMin,
            Max = SystemSettingsBounds.PodcastAudioStoreMaxGiBMax,
            FieldName = nameof(SystemSettingsUpdate.PodcastAudioStoreMaxGiB),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.PodcastAudioStoreMaxGiB),
            Read = r => r.PodcastAudioStoreMaxGiB,
            Write = (dto, v) => dto.PodcastAudioStoreMaxGiB = v,
            CacheKeyToEvict = Odyssey.Core.Internet.News.NewsSettingsLookup.CacheKey,
            // An estimate, not a measurement: spoken-word audio runs at roughly 50–100 MB an hour.
            Advise = SettingAdvisories.AboveDefault(
                dto => dto.PodcastAudioStoreMaxGiB, SystemSettingsDefaults.PodcastAudioStoreMaxGiB,
                "Each GiB holds roughly 10–20 hours of podcast audio, on the same volume as household files."),
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.PodcastAudioMaxEpisodeMb,
            Min = SystemSettingsBounds.PodcastAudioMaxEpisodeMbMin,
            Max = SystemSettingsBounds.PodcastAudioMaxEpisodeMbMax,
            FieldName = nameof(SystemSettingsUpdate.PodcastAudioMaxEpisodeMb),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.PodcastAudioMaxEpisodeMb),
            Validator = (value, ceilings) => ceilings.ValidatePodcastAudioMaxEpisodeMb(value),
            Read = r => r.PodcastAudioMaxEpisodeMb,
            Write = (dto, v) => dto.PodcastAudioMaxEpisodeMb = v,
            CacheKeyToEvict = Odyssey.Core.Internet.News.NewsSettingsLookup.CacheKey,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.PodcastAutoDownloadsPerFeedPerDay,
            Min = SystemSettingsBounds.PodcastAutoDownloadsPerFeedPerDayMin,
            Max = SystemSettingsBounds.PodcastAutoDownloadsPerFeedPerDayMax,
            FieldName = nameof(SystemSettingsUpdate.PodcastAutoDownloadsPerFeedPerDay),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.PodcastAutoDownloadsPerFeedPerDay),
            Read = r => r.PodcastAutoDownloadsPerFeedPerDay,
            Write = (dto, v) => dto.PodcastAutoDownloadsPerFeedPerDay = v,
            CacheKeyToEvict = Odyssey.Core.Internet.News.NewsSettingsLookup.CacheKey,
        },
        new IntSetting
        {
            Key = SystemSettingsKeys.PodcastAutoDownloadMaxUnfetchedPerFeed,
            Min = SystemSettingsBounds.PodcastAutoDownloadMaxUnfetchedPerFeedMin,
            Max = SystemSettingsBounds.PodcastAutoDownloadMaxUnfetchedPerFeedMax,
            FieldName = nameof(SystemSettingsUpdate.PodcastAutoDownloadMaxUnfetchedPerFeed),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.PodcastAutoDownloadMaxUnfetchedPerFeed),
            Read = r => r.PodcastAutoDownloadMaxUnfetchedPerFeed,
            Write = (dto, v) => dto.PodcastAutoDownloadMaxUnfetchedPerFeed = v,
            CacheKeyToEvict = Odyssey.Core.Internet.News.NewsSettingsLookup.CacheKey,
        },
    ];

    private static SystemSettingDescriptor[] FileObjectStorage() =>
    [
        // ── Filesystem object storage (issue #346 §3.9) ──────────────────────────────────────────
        //
        // The SECURITY claim, so AuditChanges is true and every change writes an audit line: the grace
        // is the retention period for deleted personal data. No cache key — the worker's maintenance job
        // reads it uncached once per tick, through IntSettingResolver, and maps the outcome itself.
        new IntSetting
        {
            Key = SystemSettingsKeys.FileStorageDeletionGraceHours,
            Min = SystemSettingsBounds.FileStorageDeletionGraceHoursMin,
            Max = SystemSettingsBounds.FileStorageDeletionGraceHoursMax,
            FieldName = nameof(SystemSettingsUpdate.FileStorageDeletionGraceHours),
            RequiredClaim = PermissionClaims.SystemSettingsSecurityUpdate,
            DefaultValue = Int(SystemSettingsDefaults.FileStorageDeletionGraceHours),
            Read = r => r.FileStorageDeletionGraceHours,
            Write = (dto, v) => dto.FileStorageDeletionGraceHours = v,
            Advise = SettingAdvisories.DeletionGraceOutsideBackupWindow,
        },
    ];

    private static SystemSettingDescriptor[] BackgroundJobs() =>
    [
        // ── Background jobs (issue #409 §3.6, §8) ────────────────────────────────────────────────
        //
        // No cache key: the worker's JobRunMaintenanceJob reads it uncached once an hour, through
        // IntSettingResolver, with its own degraded direction (the larger value — never prune more on a fault).
        new IntSetting
        {
            Key = SystemSettingsKeys.JobRunRetentionDays,
            Min = SystemSettingsBounds.JobRunRetentionDaysMin,
            Max = SystemSettingsBounds.JobRunRetentionDaysMax,
            FieldName = nameof(SystemSettingsUpdate.JobRunRetentionDays),
            RequiredClaim = PermissionClaims.SystemSettingsUpdate,
            DefaultValue = Int(SystemSettingsDefaults.JobRunRetentionDays),
            Read = r => r.JobRunRetentionDays,
            Write = (dto, v) => dto.JobRunRetentionDays = v,
        },
    ];
}
