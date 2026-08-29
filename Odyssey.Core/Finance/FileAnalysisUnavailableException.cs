namespace Odyssey.Core.Finance;

/// <summary>
/// The stored file-analysis model or base URL is unusable, so the analysis <strong>refuses</strong>
/// rather than substituting the compiled default (issue #439 §11).
///
/// <para>
/// It shares <see cref="FeatureDisabledException"/>'s <c>503</c> and its arm in
/// <c>GlobalExceptionHandler</c> — ordered before the generic <c>DomainException</c> arm — but carries
/// its own <see cref="Code"/>, so a client can tell "an administrator turned this off" from "the
/// server has a configuration problem", and so the existing <c>feature_disabled</c> assertions stay
/// exact.
/// </para>
///
/// <para>
/// <strong>Why refuse rather than fall back.</strong> Substituting the default model would stamp
/// <c>FileAnalysisJob.AnalyzerModel</c> with a model that did not run — the provenance corruption
/// issue #421 Non-Goal 6 was protecting against. Substituting the default base URL would transfer a
/// document to <c>api.anthropic.com</c> when the administrator had deliberately pointed the deployment
/// at a gateway: a transfer to a processor neither they nor the consenting user chose.
/// </para>
///
/// <para>
/// <strong>The detail is static text.</strong> It never names the stored value, the host or the parse
/// error; the diagnosis goes to the server log, exactly as <c>FileAnalysisSettingsLookup</c> already
/// does for the privacy-notice URL.
/// </para>
/// </summary>
public sealed class FileAnalysisUnavailableException : FeatureDisabledException
{
    public const string ConfigurationCode = "configuration_unavailable";

    public override string? Code => ConfigurationCode;

    public FileAnalysisUnavailableException()
        : base("Document analysis is temporarily unavailable while the server recovers a configuration problem.")
    {
    }
}
