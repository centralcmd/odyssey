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

    // ── The three authentication-perimeter toggles (issue #349) ──────────────────────────────────

    public const bool RequireTwoFactor = false;
    public const bool RegistrationRequireAdminApproval = true;
    public const bool EmailRequireConfirmation = true;

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
    // The calendar surface streams its export and its import (issue #516), so it ships like the task and
    // journal ones: unlimited on counts and at its 10 GB ceiling on size. The byte caps, the component caps and
    // the batch size bound memory now, not the count.
    public const string CalendarIcsMaxExportEvents = Unlimited;
    public const string CalendarIcsMaxImportEvents = Unlimited;
    public const int CalendarIcsMaxImportMegabytes = 10240;
    public const int CalendarIcsMaxExportMegabytes = 10240;
    // The task surface ships unlimited on counts and at its 10 GB ceiling on size: its files carry attached files
    // inline, and a mostly single-user instance should not meet an arbitrary cap moving its own board.
    public const string TaskIcsMaxExportTasks = Unlimited;
    public const string TaskIcsMaxImportTasks = Unlimited;
    public const int TaskIcsMaxImportMegabytes = 10240;
    public const int TaskIcsMaxExportMegabytes = 10240;
    // The journal surface follows the task one: entries carry their files and photos inline.
    public const string JournalIcsMaxExportRows = Unlimited;
    public const string JournalIcsMaxImportEntries = Unlimited;
    public const int JournalIcsMaxImportMegabytes = 10240;
    public const int JournalIcsMaxExportMegabytes = 10240;

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

    // The SMTP transport and the public link origin (issue #8). These four mirror the shipped
    // EmailOptions defaults the class carried before it was deleted — with one difference that is
    // the whole point: there is NO configuration to fall back to any more, so an empty host is not a
    // stand-in for "read it from the environment", it IS the value, and it means mail is not
    // configured on this deployment. A fresh deployment therefore sends nothing until an
    // administrator sets a host at /settings; that window is documented in docs/deployment.md and
    // surfaced on the settings page itself.
    //
    // Note what these defaults do and do NOT govern. They are what a MISSING row falls back to on
    // the GET/projection path, so the settings page can render a row for a database whose seed has
    // not run. The SEND path never consults them: it distinguishes absent (unconfigured, skip) from
    // unusable (degraded, fail closed) and substitutes nothing in either case.

    /// <summary>Empty — mail is not configured until an administrator sets a relay.</summary>
    public const string EmailSmtpHost = "";

    public const int EmailSmtpPort = 587;

    /// <summary>True: STARTTLS submission on 587 is the shipped posture.</summary>
    public const bool EmailUseStartTls = true;

    /// <summary>Empty — links cannot be composed until an administrator sets the public origin.</summary>
    public const string EmailClientBaseUrl = "";

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

    /// <summary>
    /// Term rows on one contract (issue #135). Deliberately an order of magnitude above the parties
    /// and files caps: a term series grows by <em>supersession</em>, so a contract revising one
    /// monthly fee quarterly accumulates rows for the lifetime of the agreement. The cap has to
    /// bound abuse without bounding legitimate longevity.
    /// </summary>
    public const int ContractMaxTermsPerContract = 500;
    public const int ContractMaxSummaryContracts = 1000;

    /// <summary>
    /// How many days ahead an Active contract's end date reads as "ending soon" — the cliff the
    /// header signal and the "Ending soon · Nd" summary row are about. It also bounds the mirror
    /// groups the same window defines: a term about to begin (Starting soon) and one that ran out
    /// just behind us (Recently expired). A degraded read resolves to <c>min</c>: the window drives
    /// no work, so under-reporting a cliff is preferable to inventing one.
    /// </summary>
    public const int ContractEndingWindowDays = 45;

    /// <summary>
    /// How many days ahead a contract's next recurring charge is surfaced. Same shipped value as
    /// <see cref="ContractEndingWindowDays"/>: the in-force Fee terms are stepped forward at read
    /// time and never written back, so a degraded read resolves to <c>min</c> — under-reporting a
    /// charge rather than inventing one.
    /// </summary>
    public const int ContractChargeWindowDays = 45;

    /// <summary>
    /// Next-charge rows the page-header panel lists. Bounded far below the materialised-fetch caps
    /// (see <c>SystemSettingsBounds.ContractMaxSummaryChargesMax</c>): each row is a separate
    /// rendered block in an always-open header region.
    /// </summary>
    public const int ContractMaxSummaryCharges = 6;
    public const int PhotoMaxLinksPerKind = 50;
    public const int PhotoMaxAlbumMembers = 1000;
    public const int JournalEntryMaxLinksPerKind = 50;
    public const int JournalTaskMaxLinksPerKind = 50;

    /// <summary>Files, and separately photos, one calendar event or series may link (issue #521 §4).</summary>
    public const int CalendarEventMaxLinksPerKind = 50;

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

    /// <summary>Hard per-match-call timeout; on timeout the job records MatchStatus = Failed.</summary>
    public const int FileAnalysisMatchTimeoutSeconds = 60;

    /// <summary>
    /// Prefix of the upload stream read for photo metadata extraction, in megabytes (issue #428 moved the
    /// read from the database to the upload before it is stored). <strong>16 is the ceiling</strong>:
    /// extraction reads a full <c>byte[]</c> of this size per photo, so it is a per-upload memory
    /// multiplier — see <c>docs/deployment.md</c>.
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

    /// <summary>
    /// Smart tags one contract may carry (issue #166). The same shipped default as
    /// <see cref="AccountMaxSmartTagsPerAccount"/>; the two <em>ranges</em> differ deliberately, see
    /// <c>SystemSettingsBounds.ContractMaxSmartTagsPerContractMax</c>.
    /// </summary>
    public const int ContractMaxSmartTagsPerContract = 20;

    /// <summary>
    /// Smart tags one property may carry (issue #167). The same shipped default as its account and
    /// contract siblings, and the same range — see
    /// <c>SystemSettingsBounds.PropertyMaxSmartTagsPerPropertyMax</c>.
    /// </summary>
    public const int PropertyMaxSmartTagsPerProperty = 20;

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

    /// <summary>No administrator guidance: the shipped extraction prompt is sent unchanged (issue #433).</summary>
    public const string FileAnalysisExtractionPromptAddition = "";

    /// <summary>No administrator guidance: the shipped match prompt is sent unchanged (issue #433).</summary>
    public const string FileAnalysisMatchPromptAddition = "";

    // ── Weather (issue #348) ─────────────────────────────────────────────────────────────────────

    /// <summary>Empty: weather is not configured until an administrator gives MET a way to reach them.</summary>
    public const string WeatherUserAgentContact = "";

    /// <summary>Once a day. A household dashboard needs no fresher forecast than that, and it keeps MET traffic minimal.</summary>
    public const int WeatherRefreshIntervalMinutes = 1440;

    public const int WeatherMaxTrackedPoints = 5;

    /// <summary>Empty: no household default weather location.</summary>
    public const string WeatherDefaultPointId = "";

    // ── Exchange-rate sync (issue #349) ─────────────────────────────────────────────────────────

    /// <summary>
    /// Empty: the sync starts at 1 January of the current UTC year, resolved at run time. On 1 January
    /// the default moves forward a year; nothing already fetched is deleted.
    /// </summary>
    public const string ExchangeRateHistoryStartDate = "";

    /// <summary>Hours between scheduled runs once nothing is left to catch up on.</summary>
    public const int ExchangeRateSyncIntervalHours = 24;

    // ── Instrument prices (issue #446 §8.4) ─────────────────────────────────────────────────────

    /// <summary>A watchlist price older than this many days reads as stale — a long weekend plus a holiday.</summary>
    public const int InstrumentPriceStaleAfterDays = 5;

    /// <summary>Hours between scheduled price-sync runs once every binding has caught up.</summary>
    public const int InstrumentPriceSyncIntervalHours = 24;

    /// <summary>How many days back a new source binding backfills, unless it names its own start date.</summary>
    public const int InstrumentPriceSyncHistoryDays = 365;

    // ── Filesystem object storage (issue #346) ───────────────────────────────────────────────────

    /// <summary>
    /// A day: enough for a nightly backup to copy the file volumes after the database dump, so a restored
    /// backup never references a file it does not contain.
    /// </summary>
    public const int FileStorageDeletionGraceHours = 24;

    // ── Background jobs (issue #409) ──────────────────────────────────────────────────────────────

    /// <summary>Ninety days of finished runs: a quarter of history, well under 20 000 rows with two workers (§10).</summary>
    public const int JobRunRetentionDays = 90;

    // ── News reader (issue #470 §11.1) ───────────────────────────────────────────────────────────

    /// <summary>Minutes between fetches of a feed that names no interval of its own.</summary>
    public const int NewsDefaultFetchIntervalMinutes = 60;

    /// <summary>Feeds the household may subscribe to; archived feeds count.</summary>
    public const int NewsMaxFeeds = 200;

    public const int NewsImageMaxPerArticle = 5;

    /// <summary>The largest image downloaded, in KiB.</summary>
    public const int NewsImageMaxSizeKb = 5120;

    /// <summary>The image store quota; at it, new downloads are deferred, never purged.</summary>
    public const int NewsImageStoreMaxGiB = 50;

    /// <summary>The text-and-source quota; at it, new articles are not stored, nothing is deleted.</summary>
    public const int NewsTextStoreMaxGiB = 20;

    public const int NewsFeedMaxArticlesPerDay = 500;

    public const int NewsFeedMaxImageMbPerDay = 200;

    /// <summary>Empty: every default-blocked address stays blocked. A degraded read resolves to this too.</summary>
    public const string NewsPrivateHostAllowList = "";

    // ── News OPML import (issue #508 §8.5) ────────────────────────────────────────────────────────

    /// <summary>The largest OPML file one import reads, in MB. The reader streams, so this bounds the upload, not memory.</summary>
    public const int NewsOpmlMaxImportMegabytes = 64;

    /// <summary>The most distinct feeds one OPML file may name. Its range's ceiling is this value: it can only be lowered.</summary>
    public const int NewsOpmlMaxImportEntries = 100_000;
}
