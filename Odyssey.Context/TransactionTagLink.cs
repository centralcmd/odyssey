using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Context;

// Join entity for the many-to-many between Transaction and TransactionTag. Mirrors the
// TaxStatementTag precedent: an explicit join row so a transaction can carry many tags.
// The (TransactionId, TransactionTagId) pair is unique to prevent duplicate links.
[Index(nameof(TransactionId), nameof(TransactionTagId), IsUnique = true)]
public class TransactionTagLink
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [Required]
    public required Guid TransactionId { get; set; }

    public Transaction? Transaction { get; set; }

    [Required]
    public required Guid TransactionTagId { get; set; }

    public TransactionTag? TransactionTag { get; set; }
}
