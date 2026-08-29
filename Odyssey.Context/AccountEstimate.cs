using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

/// <summary>
/// A time-versioned entry recording a user-supplied estimated value for an account whose worth is not
/// derived from transactions (e.g. a property or a vehicle), effective from a given date. There is no
/// explicit end date: the value in force on a date is the entry with the greatest
/// <see cref="EffectiveFrom"/> on or before it. The composite index backs both history listing and
/// current-value resolution. Modelled on <see cref="AccountTerm"/>, minus the kind/unit/billing
/// dimensions — an estimate is always a single money amount in the account currency.
/// </summary>
[Index(nameof(AccountId), nameof(EffectiveFrom))]
public class AccountEstimate : IEffectiveDated
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid AccountEstimateId { get; set; }

    [Required]
    public Guid AccountId { get; set; }

    public Account Account { get; set; } = null!;

    [Required]
    [Precision(18, 6)]
    public decimal Value { get; set; }

    // Retained for storage simplicity, but always equal to the account currency (enforced by the API/GUI).
    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    [Required]
    public DateTime EffectiveFrom { get; set; }

    [StringLength(512)]
    public string? Note { get; set; }

    [Required]
    public DateTime CreatedAtUtc { get; set; }
}
