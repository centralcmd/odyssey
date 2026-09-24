using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// The currently-effective value of a single series on an account — the entry with the greatest
/// <c>EffectiveFrom</c> on or before the resolution date within its series, which is its label.
/// </summary>
public sealed record CurrentTerm
{
    /// <summary>The series name, as the user wrote it. Null for a rate.</summary>
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
}
