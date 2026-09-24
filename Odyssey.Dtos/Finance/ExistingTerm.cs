using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record ExistingTerm
{
    public required Guid TermId { get; set; }

    /// <summary>
    /// The owning contract — the only owner a term has since issue #190. Taken from the route and on no
    /// request DTO, so it is not forgeable from a body.
    /// </summary>
    public Guid ContractId { get; set; }

    /// <summary>The series name, as the user wrote it.</summary>
    [StringLength(TermLabel.MaxLength)]
    public string? Label { get; set; }

    public TermValueUnit ValueUnit { get; set; }

    /// <summary>Which way the money moves, from the household's perspective (issue #159).</summary>
    public TermDirection Direction { get; set; }

    /// <summary>
    /// The numeric value of a <c>Percentage</c> or <c>Amount</c> term; null on a <c>Text</c> or
    /// <c>DateTime</c> term (issue #192). Exactly one of this, <see cref="TextValue"/> and
    /// <see cref="DateTimeValue"/> is set, and it is the one <see cref="ValueUnit"/> names.
    /// </summary>
    public decimal? Value { get; set; }

    /// <summary>A <c>Text</c> term's value, trimmed; null on every other kind.</summary>
    [StringLength(TermTextValue.MaxLength)]
    public string? TextValue { get; set; }

    /// <summary>A <c>DateTime</c> term's value, in UTC; null on every other kind.</summary>
    public DateTime? DateTimeValue { get; set; }

    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    /// <summary>The cadence unit; null for a rate.</summary>
    public Interval? Interval { get; set; }

    /// <summary>The cadence multiplier — non-null iff <see cref="Interval"/> is periodic.</summary>
    public int? IntervalCount { get; set; }

    /// <summary>When the term is first actually charged, if that differs from <see cref="EffectiveFrom"/>.</summary>
    public DateTime? AnchorDate { get; set; }
    public DateTime EffectiveFrom { get; set; }

    [StringLength(512)]
    public string? Note { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
