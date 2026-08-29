using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

/// <summary>
/// A matched transaction tag for a candidate — modelled as a child join table (queryable, FK-safe,
/// consistent with <see cref="TransactionTagLink"/>) because a candidate may carry 0..N matched tags.
/// Both FKs cascade-delete: a tag link is meaningless once either side is gone.
/// </summary>
[PrimaryKey(nameof(CandidateTransactionId), nameof(TransactionTagId))]
public class FileAnalysisCandidateTag
{
    [Required]
    public Guid CandidateTransactionId { get; set; }

    [ForeignKey(nameof(CandidateTransactionId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public FileAnalysisCandidateTransaction? CandidateTransaction { get; set; }

    [Required]
    public Guid TransactionTagId { get; set; }

    [ForeignKey(nameof(TransactionTagId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public TransactionTag? TransactionTag { get; set; }
}
