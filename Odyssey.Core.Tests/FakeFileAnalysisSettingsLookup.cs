using Odyssey.Core.Finance;

namespace Odyssey.Core.Tests;

/// <summary>
/// Test double for <see cref="IFileAnalysisSettingsLookup"/> (issue #421 Wave 1).
///
/// <para>
/// This is the reason that interface lives in <c>Odyssey.Core.Finance</c> rather than beside its
/// implementation: <c>Odyssey.Core.Tests</c> runs on EF InMemory and has <em>no reference</em> to
/// <c>Odyssey.Context</c>, so it fakes the lookup instead of seeding settings rows.
/// </para>
///
/// <para>
/// The defaults below are therefore literals rather than a reference to
/// <c>SystemSettingsDefaults.*</c> — importing that project here would defeat the boundary this
/// interface exists to preserve. They mirror the shipped seed, so a test that does not care about
/// settings reads production behaviour; <c>SystemSettingKindTests</c> and the API-tier settings tests
/// are what pin the real values.
/// </para>
/// </summary>
internal sealed class FakeFileAnalysisSettingsLookup : IFileAnalysisSettingsLookup
{
    public FileAnalysisSettings Settings { get; set; } = new(
        Processor: "Anthropic",
        ProcessorRegion: "United States",
        LawfulBasis: "Consent · GDPR Art. 6(1)(a)",
        PrivacyNoticeUrl: "https://www.anthropic.com/legal/privacy",
        MaxFutureTransactionDays: 90,
        AutoLinkThreshold: 0.60m,
        MaxTokens: 8096,
        MatchMaxVocabulary: 500,
        MatchTimeoutSeconds: 60,
        Model: "claude-sonnet-5",
        BaseUrl: "https://api.anthropic.com",
        IsDegraded: false);

    /// <summary>
    /// The kill switch (issue #439). Defaults to <see langword="true"/> because these tests exercise
    /// what analysis <em>does</em>; the disabled path has its own tests that flip it. That is the
    /// opposite default from production, where a fresh install ships analysis off — deliberately, since
    /// a fake defaulting to "off" would make every unrelated test in this file assert a 503.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Counts the live reads, so a test can pin that the switch is never served from a cache.</summary>
    public int EnabledReadCount { get; private set; }

    public Task<FileAnalysisSettings> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Settings);

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        EnabledReadCount++;
        return Task.FromResult(Enabled);
    }
}
