using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Context;

/// <summary>
/// Join entity associating a <see cref="Property"/> with a <see cref="TransactionTag"/> as one of the
/// property's "smart tags" (issue #167): a curated, persistent saved filter surfacing the transactions
/// that carry any of these tags. Mirrors <see cref="ContractSmartTag"/> exactly — the composite key
/// <c>(PropertyId, TransactionTagId)</c> enforces one association per pair, and <see cref="AddedAt"/> is
/// indexed to back stable insertion ordering.
///
/// <para>
/// A sibling table rather than a nullable <c>PropertyId</c> on an existing one, for the reason
/// <see cref="ContractSmartTag"/> records: a polymorphic table loses the composite primary key, makes
/// both foreign keys optional, and turns two different cascade behaviours into application-code
/// vigilance.
/// </para>
/// </summary>
[Index(nameof(AddedAt))]
public class PropertySmartTag
{
    [Required]
    public Guid PropertyId { get; set; }

    public Property Property { get; set; } = null!;

    [Required]
    public Guid TransactionTagId { get; set; }

    public TransactionTag TransactionTag { get; set; } = null!;

    [Required]
    public DateTime AddedAt { get; set; }
}
