namespace Odyssey.Dtos.Finance;

public sealed record ExistingFileAnalysisCandidateTransaction(
    Guid Id,
    DateTime TransactionDate,
    DateTime? BookingDate,
    string Description,
    string? Merchant,
    string? CategoryHint,
    decimal Amount,
    string Currency,
    string? ExternalId,
    string? ReferenceNumber,
    decimal? LlmConfidence,
    string? LlmModel,
    CandidateTransactionReviewStatus ReviewStatus,
    DateTime? ReviewedAt,
    // ── AI matching (issue #266) ──
    Guid? MatchedContactId,
    string? MatchedContactName,
    List<Guid> MatchedTagIds,
    decimal? MerchantMatchConfidence,
    decimal? CategoryMatchConfidence,
    MatchMethod MatchMethod
);
