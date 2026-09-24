using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// One in-force term on a contract — the projection the record card's "Current" band renders on
/// <see cref="ExistingContract"/>. The name predates issue #190, when accounts could own terms too;
/// it is kept rather than renamed (issue #190 Non-Goal 5). There is at most one entry per
/// SERIES — its <c>Label</c> — being the term with the latest <c>EffectiveFrom</c> on or
/// before today, which is what "in force" means for a series that is a run of supersessions.
/// </summary>
public sealed record AccountCurrentTerm
{
    /// <summary>
    /// The series name, as the user wrote it; null for a rate. This projection deliberately carries
    /// <c>Label</c> and not <c>Note</c>: the label is the tile's <em>name</em>, without which a card
    /// renders several indistinguishable tiles, and it is bounded at 64 characters against Note's 512.
    /// </summary>
    [StringLength(TermLabel.MaxLength)]
    public string? Label { get; set; }

    public TermValueUnit ValueUnit { get; set; }

    /// <summary>
    /// Which way the money moves, from the household's perspective (issue #159). Carried here because
    /// <c>ExistingContract.CurrentTerms</c> is the feature's primary read.
    /// </summary>
    public TermDirection Direction { get; set; }

    public decimal Value { get; set; }

    /// <summary>Set for a money-valued term; null for a percentage.</summary>
    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    /// <summary>
    /// How often a money-valued term is charged — the cadence unit. It is what separates a 695
    /// annual fee from a 695 monthly one, so the card shows it beside the date rather than dropping
    /// it. Read with <see cref="IntervalCount"/>: the two are one cadence.
    /// </summary>
    public Interval? Interval { get; set; }

    /// <summary>The cadence multiplier — non-null iff <see cref="Interval"/> is periodic.</summary>
    public int? IntervalCount { get; set; }

    /// <summary>When the term is first actually charged, if that differs from <see cref="EffectiveFrom"/>.</summary>
    public DateTime? AnchorDate { get; set; }

    /// <summary>When this term took effect — the "since" the card's tile foot carries.</summary>
    public DateTime EffectiveFrom { get; set; }
}
