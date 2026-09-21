using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Context;

/// <summary>
/// Join entity associating a <see cref="Contract"/> with a <see cref="TransactionTag"/> as one of the
/// contract's "smart tags": a curated, persistent saved filter that surfaces the transactions carrying
/// any of these tags, so a contract's real spend is readable in place (issue #166). The composite key
/// <c>(ContractId, TransactionTagId)</c> enforces a single association per pair; <see cref="AddedAt"/>
/// is indexed to back stable insertion ordering.
///
/// <para>
/// A sibling table of <see cref="AccountSmartTag"/> rather than a nullable <c>ContractId</c> added to
/// it: a polymorphic table loses the composite primary key, makes both foreign keys optional, and
/// turns two different cascade behaviours into application-code vigilance.
/// </para>
/// </summary>
[Index(nameof(AddedAt))]
public class ContractSmartTag
{
    [Required]
    public Guid ContractId { get; set; }

    public Contract Contract { get; set; } = null!;

    [Required]
    public Guid TransactionTagId { get; set; }

    public TransactionTag TransactionTag { get; set; } = null!;

    [Required]
    public DateTime AddedAt { get; set; }
}
