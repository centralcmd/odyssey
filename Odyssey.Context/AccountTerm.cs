using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

/// <summary>
/// A time-versioned entry recording the value of one <see cref="Context.TermKind"/> for an account
/// (an interest rate, an expected return, or the price of a bank service) effective from a given
/// date. There is no explicit end date: the value in force on a date is the entry with the greatest
/// <see cref="EffectiveFrom"/> on or before it. The composite index backs both history listing and
/// current-value resolution.
/// </summary>
[Index(nameof(AccountId), nameof(TermKind), nameof(EffectiveFrom))]
public class AccountTerm : IEffectiveDated
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid AccountTermId { get; set; }

    [Required]
    public Guid AccountId { get; set; }

    public Account Account { get; set; } = null!;

    [Required]
    public TermKind TermKind { get; set; }

    [Required]
    public TermValueUnit ValueUnit { get; set; }

    [Required]
    [Precision(18, 6)]
    public decimal Value { get; set; }

    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    public BillingPeriod? BillingPeriod { get; set; }

    [Required]
    public DateTime EffectiveFrom { get; set; }

    [StringLength(512)]
    public string? Note { get; set; }

    [Required]
    public DateTime CreatedAtUtc { get; set; }
}
