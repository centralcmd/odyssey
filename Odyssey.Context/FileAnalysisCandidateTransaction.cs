using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

[Index(nameof(AnalysisJobId))]
[Index(nameof(ReviewStatus))]
public class FileAnalysisCandidateTransaction
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [Required]
    public required Guid AnalysisJobId { get; set; }

    [ForeignKey(nameof(AnalysisJobId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public FileAnalysisJob? AnalysisJob { get; set; }

    [Required]
    public required DateTime TransactionDate { get; set; }

    public DateTime? BookingDate { get; set; }

    [Required]
    [StringLength(1024)]
    public required string Description { get; set; }

    [StringLength(512)]
    public string? Merchant { get; set; }

    [StringLength(256)]
    public string? CategoryHint { get; set; }

    [Required]
    [Precision(18, 6)]
    public required decimal Amount { get; set; }

    [Required]
    [StringLength(3)]
    public required string Currency { get; set; }

    [StringLength(256)]
    public string? ExternalId { get; set; }

    [StringLength(256)]
    public string? InternalId { get; set; }

    [StringLength(256)]
    public string? ReferenceNumber { get; set; }

    public int? SourceLineNumber { get; set; }

    public int? SourcePageNumber { get; set; }

    [Precision(5, 4)]
    public decimal? LlmConfidence { get; set; }

    [StringLength(256)]
    public string? LlmModel { get; set; }

    [StringLength(256)]
    public string? LlmProviderResponseId { get; set; }

    public string? LlmRawJson { get; set; }

    [Required]
    public CandidateTransactionReviewStatus ReviewStatus { get; set; } = CandidateTransactionReviewStatus.Pending;

    public DateTime? ReviewedAt { get; set; }

    public string? ReviewedByUserId { get; set; }

    // ── AI merchant/category matching (issue #266) ──────────────────────────────
    // The free-text Merchant/CategoryHint above are kept verbatim (the audit/display
    // source of truth); these resolve them to existing records without overwriting them.

    /// <summary>The AI-suggested or user-chosen contact; null = unmatched. A real FK to <c>Contact</c>
    /// with <c>ON DELETE SET NULL</c>, declared in <see cref="OdysseyContext"/>; also cleared via IContactReferenceGuard on contact
    /// delete (previously a real ON DELETE SET NULL).</summary>
    public Guid? MatchedContactId { get; set; }

    /// <summary>Match confidence for the merchant → contact link (0.0–1.0); null when no match.</summary>
    [Precision(5, 4)]
    public decimal? MerchantMatchConfidence { get; set; }

    /// <summary>Match confidence for the category → tag link (0.0–1.0); null when no match.</summary>
    [Precision(5, 4)]
    public decimal? CategoryMatchConfidence { get; set; }

    /// <summary>Provenance of the currently stored contact/tag values.</summary>
    [Required]
    public MatchMethod MatchMethod { get; set; } = MatchMethod.None;

    public ICollection<FileAnalysisCandidateTag> MatchedTags { get; set; } = new List<FileAnalysisCandidateTag>();
}
