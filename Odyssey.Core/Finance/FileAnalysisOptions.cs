using Odyssey.Dtos;

namespace Odyssey.Core.Finance;

public class FileAnalysisOptions
{
    public const string SectionName = "FileAnalysis";

    // Moved to the system-settings store (issue #439). Nothing reads these three any more — the
    // lookup resolves its own values, the kill switch through IFileAnalysisSettingsLookup.IsEnabledAsync
    // (deliberately NOT on FileAnalysisSettings, so it cannot be consumed from a cache) and the model
    // and destination through the per-run snapshot, passed to the provider as FileAnalysisTarget so the
    // value a request was built with is the value stamped on the job.
    //
    // They survive as documentation of record — the one place a reader can see what these values are
    // without reading a migration — and their initialisers name the SHARED constants rather than
    // restating literals, which is what stops that documentation drifting from the seed. That is
    // exactly the mistake Wave 1 had to correct on MaxFutureTransactionDays.
    public bool Enabled { get; set; } = SystemSettingsDefaults.FileAnalysisEnabled;
    public string Provider { get; set; } = "Claude";
    public string BaseUrl { get; set; } = SystemSettingsDefaults.FileAnalysisBaseUrl;
    // ApiKey is GONE (issue #445 Wave 1). It moved to the encrypted secret store as
    // SecretSettingKeys.FileAnalysisApiKey and is attached per request by FileAnalysisApiKeyHandler.
    //
    // Deleted rather than kept as documentation of record, which is what the moved PLAINTEXT settings
    // above do. The difference is that a surviving property is a fallback waiting to be written: the
    // one rule this migration exists to hold is that an unreadable row never resolves to the
    // configured value, and the cheapest way to guarantee that is to leave nothing to resolve to.
    public string Model { get; set; } = SystemSettingsDefaults.FileAnalysisModel;
    // Stays in deploy-time config (issue #434): consumed once at startup inside
    // .AddStandardResilienceHandler(), so a runtime value could never reach a live pipeline — and worse
    // than inert, since the options validator rejects the handler unless SamplingDuration >= 2x
    // AttemptTimeout. Contrast Match.TimeoutSeconds below, which IS per-call and did move.
    public int TimeoutSeconds { get; set; } = 120;
    public string PromptVersion { get; set; } = "1.0";
    public string PromptTemplatePath { get; set; } = "Resources/Prompts/transaction-extraction.txt";
    // Moved to the system-settings store (issue #434 key 1); read through IFileAnalysisSettingsLookup
    // and passed to the provider as a parameter, so the value a request was built with is the value
    // stamped on the job.
    //
    // The default is INITIALISED FROM the shared constant rather than restated. Nothing reads this
    // property any more — the lookup resolves its own fallback from SystemSettingsDefaults — so the
    // value here is documentation of record, and initialising it from the same constant is what stops
    // that documentation drifting from the seed. That is precisely the mistake Wave 1 had to correct on
    // MaxFutureTransactionDays, where appsettings.json shipped 90 and this class said 30.
    public int MaxTokens { get; set; } = SystemSettingsDefaults.FileAnalysisMaxTokens;
    // Moved to the system-settings store (issue #421 Wave 1); read through
    // IFileAnalysisSettingsLookup. Kept only as the compiled fallback, and corrected from 30 to 90:
    // appsettings.json shipped 90, this class said 30, and 90 is what actually ran.
    public int MaxFutureTransactionDays { get; set; } = 90;

    // ── Privacy / consent disclosure (the analysis transfer goes to a third party) ──
    public string Processor { get; set; } = "Anthropic";
    public string ProcessorRegion { get; set; } = "United States";
    // The four disclosure values and the lawful basis moved to the system-settings store (issue #421
    // Wave 1) and are served to the consent gate by GET /api/file-analysis/disclosure. They remain
    // here only as compiled fallbacks for a degraded read.
    public string LawfulBasis { get; set; } = "Consent · GDPR Art. 6(1)(a)";
    public string PrivacyNoticeUrl { get; set; } = "https://www.anthropic.com/legal/privacy";

    // ── AI matching step (issue #266) ──
    //
    // The FileAnalysis:Match SECTION no longer exists in appsettings.json: AutoLinkThreshold moved to
    // the settings store in issue #421 Wave 1, and MaxVocabulary/TimeoutSeconds followed in issue #434
    // (keys 2 and 3). The class survives — binding an absent section is now the expected case — for the
    // same reason Wave 1 kept the disclosure strings here: it is the one place a reader can see what
    // these values are without reading a migration.
    public MatchOptions Match { get; set; } = new();
}

public class MatchOptions
{
    /// <summary>A returned match ≥ this is auto-linked (MatchMethod=Llm); below it is a suggestion only.</summary>
    public double AutoLinkThreshold { get; set; } = 0.60;

    /// <summary>
    /// Per-list cap (contacts, tags). Over cap ⇒ the LLM match is skipped, not truncated.
    /// Database-backed since issue #434; the value here is documentation of record, initialised from the
    /// same shared constant the migration seeds so the two cannot disagree.
    /// </summary>
    public int MaxVocabulary { get; set; } = SystemSettingsDefaults.FileAnalysisMatchMaxVocabulary;

    /// <summary>
    /// Hard per-match-call timeout; on timeout ⇒ MatchStatus = Failed and manual fallback.
    /// Database-backed since issue #434; documentation of record, as above.
    ///
    /// <para>
    /// Not to be confused with <see cref="FileAnalysisOptions.TimeoutSeconds"/>, which stays in
    /// deploy-time config: that one is consumed ONCE at startup by the resilience handler, so a
    /// runtime value could never reach a live pipeline, and the options validator additionally rejects
    /// the handler unless <c>SamplingDuration &gt;= 2 x AttemptTimeout</c>. This one is per-call.
    /// </para>
    /// </summary>
    public int TimeoutSeconds { get; set; } = SystemSettingsDefaults.FileAnalysisMatchTimeoutSeconds;
}
