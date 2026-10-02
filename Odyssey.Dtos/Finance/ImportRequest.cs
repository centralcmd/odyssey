using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record ImportRequest
{
    [Required]
    public required List<ImportCandidateRequest> Candidates { get; set; }
}

/// <summary>
/// One reviewed candidate to import. <see cref="Description"/> and <see cref="ExternalId"/> are bounded
/// by the CANDIDATE columns they are prefilled from (1024 and 256), not by the narrower ledger columns:
/// the review grid sends the extracted values back verbatim, so a ledger-width bound would refuse the
/// whole batch over one long extracted description. The service fits them to the ledger on import.
/// </summary>
public sealed record ImportCandidateRequest
{
    [Required]
    public required Guid CandidateId { get; set; }

    public DateTime? TransactionDate { get; set; }

    [StringLength(DescriptionMaxLength)]
    public string? Description { get; set; }

    [Range(typeof(decimal), MoneyBounds.AmountMin, MoneyBounds.AmountMax, ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public decimal? Amount { get; set; }

    [StringLength(3)]
    public string? Currency { get; set; }

    public Guid? ContactId { get; set; }

    public List<Guid>? TransactionTagIds { get; set; }

    [StringLength(ExternalIdMaxLength)]
    public string? ExternalId { get; set; }

    /// <summary>Shared with the review grid's input, so the field stops where the server would refuse.</summary>
    public const int DescriptionMaxLength = 1024;

    /// <summary>Shared with the review grid's Reference input.</summary>
    public const int ExternalIdMaxLength = 256;
}

public sealed record ImportResponse(
    int Imported,
    int Failed,
    List<ImportFailure> Failures
);

public sealed record ImportFailure(Guid CandidateId, string Reason);
