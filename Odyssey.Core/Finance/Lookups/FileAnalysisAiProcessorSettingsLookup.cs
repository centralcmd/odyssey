using Odyssey.Core.Ai;

namespace Odyssey.Core.Finance;

/// <summary>
/// The neutral <see cref="IAiProcessorSettingsLookup"/> view over the statement-analysis settings rows
/// (#602 §3.1). The processor facts, model, base URL and output cap are one set of rows at
/// <c>/settings</c>, shared by every Claude flow; this is how a flow outside Finance reads them.
/// </summary>
public sealed class FileAnalysisAiProcessorSettingsLookup(IFileAnalysisSettingsLookup inner) : IAiProcessorSettingsLookup
{
    public async Task<AiProcessorSettings> GetAsync(CancellationToken cancellationToken = default) =>
        Map(await inner.GetAsync(cancellationToken));

    public async Task<AiProcessorSettings> GetUncachedAsync(CancellationToken cancellationToken = default) =>
        Map(await inner.GetUncachedAsync(cancellationToken));

    private static AiProcessorSettings Map(FileAnalysisSettings s) => new(
        s.Processor, s.ProcessorRegion, s.LawfulBasis, s.PrivacyNoticeUrl, s.Model, s.BaseUrl, s.MaxTokens, s.IsDegraded, s.ReadFailed);
}
