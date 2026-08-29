using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Odyssey.Context;

/// <summary>
/// Join entity associating an <see cref="Account"/> with a <see cref="TransactionTag"/> as one of the
/// account's "smart tags": a curated, persistent saved filter that surfaces the account's transactions
/// carrying any of these tags. The composite key <c>(AccountId, TransactionTagId)</c> enforces a single
/// association per pair; <see cref="AddedAt"/> is indexed to back stable insertion ordering.
/// </summary>
[Index(nameof(AddedAt))]
public class AccountSmartTag
{
    [Required]
    public Guid AccountId { get; set; }

    public Account Account { get; set; } = null!;

    [Required]
    public Guid TransactionTagId { get; set; }

    public TransactionTag TransactionTag { get; set; } = null!;

    [Required]
    public DateTime AddedAt { get; set; }
}
