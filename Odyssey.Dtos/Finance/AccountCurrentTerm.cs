using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// One in-force term on an account — the projection the record card's "Current" band renders, and the
/// only place the full set is exposed on <see cref="ExistingAccount"/>. There is at most one entry per
/// <see cref="TermKind"/>: the term with the latest <c>EffectiveFrom</c> on or before today, which is
/// what "in force" means for a kind whose history is a series of supersessions.
/// </summary>
public sealed record AccountCurrentTerm
{
    public TermKind TermKind { get; set; }

    public TermValueUnit ValueUnit { get; set; }

    public decimal Value { get; set; }

    /// <summary>Set for a money-valued term; null for a percentage.</summary>
    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    /// <summary>
    /// How often a money-valued term is charged. It is what separates a 695 annual fee from a 695
    /// monthly one, so the card shows it beside the date rather than dropping it.
    /// </summary>
    public BillingPeriod? BillingPeriod { get; set; }

    /// <summary>When this term took effect — the "since" the card's tile foot carries.</summary>
    public DateTime EffectiveFrom { get; set; }
}
