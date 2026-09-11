using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

/// <summary>
/// A time-versioned entry recording the value of one named series on an account (an interest rate, an
/// expected return, or the price of one bank service) effective from a given date. The series key is
/// <c>(AccountId, TermKind, LabelKey)</c>, so one kind can hold several concurrently in-force terms
/// told apart by their labels. There is no explicit end date: the value in force on a date is the
/// entry with the greatest <see cref="EffectiveFrom"/> on or before it <em>within its own series</em>.
/// The composite index backs both history listing and current-value resolution, and retains the
/// <c>(AccountId, TermKind)</c> prefix the kind-filtered history query uses.
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

    /// <summary>
    /// The user's own wording for this series ("ATM withdrawal · abroad"), in their own casing.
    /// Required for a <see cref="Context.TermKind.Fee"/>, null for a rate — a rate is always the
    /// unnamed series of its own kind, which is exactly how it resolved before labels existed.
    /// </summary>
    [StringLength(64)]
    public string? Label { get; set; }

    /// <summary>
    /// <see cref="Label"/> normalized and case-folded — the column that participates in the series
    /// key. Written ONLY by the domain service and never bound from a request. It is persisted rather
    /// than folded at query time because MariaDB's default collation is case-insensitive while the EF
    /// InMemory provider compares ordinally, so a predicate over <see cref="Label"/> would mean
    /// different things on the two test tiers.
    /// </summary>
    [StringLength(64)]
    public string? LabelKey { get; set; }

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
