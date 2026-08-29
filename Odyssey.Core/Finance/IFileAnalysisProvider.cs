namespace Odyssey.Core.Finance;

/// <remarks>
/// <c>maxTokens</c> is a <strong>parameter, not something the provider reads for itself</strong>
/// (issue #434 key 1). It is database-backed now, and <see cref="FileAnalysisService"/> already takes
/// one settings snapshot per run and stamps it on the job — so passing it down is what guarantees the
/// value the request was built with is the value the audit record reports. Reading it inside the
/// provider would let a concurrent admin write separate the two.
///
/// <para>
/// <see cref="FileAnalysisTarget"/> joins it for the same reason (issue #439). The model used to come
/// from <c>IOptions&lt;FileAnalysisOptions&gt;</c> and the destination from the typed client's
/// <c>BaseAddress</c>, both fixed at startup; both are admin-editable now, and both are recorded on the
/// job — <c>AnalyzerModel</c> and <c>AnalyzerBaseUrlHost</c>. Passing them per call is what keeps the
/// stamp and the request that was actually made describing the same thing.
/// </para>
/// </remarks>
public interface IFileAnalysisProvider
{
    Task<List<ExtractedTransaction>> ExtractTransactionsAsync(
        byte[] fileContent,
        string contentType,
        string accountCurrencyCode,
        string promptTemplate,
        FileAnalysisTarget target,
        int maxTokens,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The second LLM step (issue #266): resolve each candidate's free-text merchant/category against
    /// the supplied <paramref name="contactVocabulary"/>/<paramref name="tagVocabulary"/> name
    /// lists. Each vocabulary entry carries an opaque short <c>Ref</c> token Odyssey assigns (never a
    /// GUID); the model returns those tokens, which the caller validates by set-membership and maps
    /// back to ids. Sends names only — no document content, no other fields.
    /// </summary>
    Task<List<MatchedCandidate>> MatchTransactionsAsync(
        IReadOnlyList<MatchCandidateInput> candidates,
        IReadOnlyList<VocabularyEntry> contactVocabulary,
        IReadOnlyList<VocabularyEntry> tagVocabulary,
        FileAnalysisTarget target,
        int maxTokens,
        CancellationToken cancellationToken = default);
}

public record ExtractedTransaction(
    DateTime TransactionDate,
    DateTime? BookingDate,
    string Description,
    string? Merchant,
    string? CategoryHint,
    decimal Amount,
    string? Currency,
    string? ExternalId,
    string? ReferenceNumber,
    decimal? LlmConfidence,
    string? LlmModel,
    string? LlmProviderResponseId,
    string? LlmRawJson
);

/// <summary>One extracted candidate to match, identified by its position in the sent list.</summary>
public record MatchCandidateInput(int Index, string? Merchant, string? Category);

/// <summary>A reference-list entry: an opaque token plus the name shown to the model.</summary>
public record VocabularyEntry(string Ref, string Name);

/// <summary>The model's match result for one candidate index (refs are validated by the caller).</summary>
public record MatchedCandidate(
    int Index,
    string? ContactRef,
    decimal? ContactConfidence,
    IReadOnlyList<string> TagRefs,
    decimal? CategoryConfidence
);
