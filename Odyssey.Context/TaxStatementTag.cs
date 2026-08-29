using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Finance;

namespace Odyssey.Context;

[Index(nameof(TaxStatementId), nameof(TransactionTagId), nameof(Role), IsUnique = true)]
public class TaxStatementTag
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [Required]
    public required Guid TaxStatementId { get; set; }

    [ForeignKey(nameof(TaxStatementId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public TaxStatement? TaxStatement { get; set; }

    [Required]
    public required Guid TransactionTagId { get; set; }

    [ForeignKey(nameof(TransactionTagId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public TransactionTag? TransactionTag { get; set; }

    [Required]
    public required TaxStatementTagRole Role { get; set; }
}
