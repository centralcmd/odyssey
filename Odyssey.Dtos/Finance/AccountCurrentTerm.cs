using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// One in-force term on an account — the projection the record card's "Current" band renders, and the
/// only place the full set is exposed on <see cref="ExistingAccount"/>. There is at most one entry per
/// SERIES — <c>(TermKind, Label)</c> — being the term with the latest <c>EffectiveFrom</c> on or
/// before today, which is what "in force" means for a series that is a run of supersessions.
/// </summary>
public sealed record AccountCurrentTerm
{
    public TermKind TermKind { get; set; }

    /// <summary>
    /// The series name, as the user wrote it; null for a rate. This projection deliberately carries
    /// <c>Label</c> and not <c>Note</c>: the label is the tile's <em>name</em>, without which a card
    /// renders several indistinguishable tiles, and it is bounded at 64 characters against Note's 512.
    /// </summary>
    [StringLength(TermLabel.MaxLength)]
    public string? Label { get; set; }

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
