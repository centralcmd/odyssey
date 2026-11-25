namespace Odyssey.Core.Ai;

/// <summary>
/// The processor facts and transport values every Claude flow shares (#602 §3.1): who the data goes
/// to, under which lawful basis, which model at which address, and the output cap. Statement analysis
/// owns the stored settings (they are its rows at <c>/settings</c>); this is the neutral view a second
/// flow — AI recipe import — reads without referencing Finance.
/// </summary>
/// <param name="Model">Null when the stored value could not be used — never a substituted default.</param>
/// <param name="BaseUrl">Scheme and authority only; null when unusable, for the same reason.</param>
/// <param name="IsDegraded">Any value fell back instead of being read cleanly.</param>
/// <param name="ReadFailed">The settings read itself failed.</param>
public sealed record AiProcessorSettings(
    string Processor,
    string ProcessorRegion,
    string LawfulBasis,
    string PrivacyNoticeUrl,
    string? Model,
    string? BaseUrl,
    int MaxTokens,
    bool IsDegraded,
    bool ReadFailed);

/// <summary>Reads <see cref="AiProcessorSettings"/>; implemented over the statement-analysis settings rows.</summary>
public interface IAiProcessorSettingsLookup
{
    /// <summary>The 30-second cached snapshot, for disclosure and consent text.</summary>
    Task<AiProcessorSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Read fresh, never cached — what a worker compares a consent against right before it sends.</summary>
    Task<AiProcessorSettings> GetUncachedAsync(CancellationToken cancellationToken = default);
}

/// <summary>The per-attempt transport timeout, a deploy-time value (<c>FileAnalysis:TimeoutSeconds</c>).</summary>
public sealed record AiTransportOptions(int TimeoutSeconds);

/// <summary>
/// The host part of a processor base URL, the way a transfer record stores it: scheme://host[:port]
/// for the origin, the host alone for the host. Never a path, query or user-info.
/// </summary>
public static class AiDestination
{
    public static string? Origin(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : null;

    public static string? Host(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u.Host : null;
}
