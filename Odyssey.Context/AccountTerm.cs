using Microsoft.EntityFrameworkCore;
using DtoTermLabel = Odyssey.Dtos.Finance.TermLabel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

/// <summary>
/// A time-versioned entry recording the value of one term series for an account (an interest rate,
/// an expected return, or the price of a bank service) effective from a given date. There is no
/// explicit end date: the value in force on a date is the entry with the greatest
/// <see cref="EffectiveFrom"/> on or before it.
/// <para>
/// A series is <see cref="TermKind"/> <em>plus</em> <see cref="LabelKey"/>, not the kind alone. A
/// card really does carry several fees of one kind at once — a cash-withdrawal fee that differs at
/// home and abroad, two unrelated "other" charges — and keying supersession on the kind made the
/// second silently replace the first. The composite index leads with the same
/// <c>(AccountId, TermKind)</c> prefix the kind-filtered history query uses, so widening it costs
/// that path nothing.
/// </para>
/// </summary>
[Index(nameof(AccountId), nameof(TermKind), nameof(LabelKey), nameof(EffectiveFrom))]
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

    /// <summary>
    /// The user's name for this series, in their own casing ("ATM withdrawal · abroad"). Null is the
    /// kind's unnamed series — the shape of every row written before labels existed.
    /// </summary>
    [StringLength(DtoTermLabel.MaxLength)]
    public string? Label { get; set; }

    /// <summary>
    /// <see cref="Label"/> case-folded, and the half that actually keys the series. Persisting the
    /// folded form rather than folding in the query keeps the two test tiers agreeing: MariaDB's
    /// default collation is case-insensitive, the EF InMemory provider compares ordinally, so a
    /// predicate written against <see cref="Label"/> would mean different things on each.
    /// Written only by the domain service, from the shared <c>TermLabel</c> rules.
    /// </summary>
    [StringLength(DtoTermLabel.MaxLength)]
    public string? LabelKey { get; set; }

    [Required]
    public DateTime EffectiveFrom { get; set; }

    [StringLength(512)]
    public string? Note { get; set; }

    [Required]
    public DateTime CreatedAtUtc { get; set; }
}
