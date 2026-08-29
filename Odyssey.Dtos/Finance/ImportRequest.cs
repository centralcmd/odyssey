using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record ImportRequest(
    [Required] List<ImportCandidateRequest> Candidates
);

public sealed record ImportCandidateRequest(
    [Required] Guid CandidateId,
    DateTime? TransactionDate,
    string? Description,
    decimal? Amount,
    string? Currency,
    Guid? ContactId = null,
    List<Guid>? TransactionTagIds = null,
    string? ExternalId = null
);

public sealed record ImportResponse(
    int Imported,
    int Failed,
    List<ImportFailure> Failures
);

public sealed record ImportFailure(Guid CandidateId, string Reason);
