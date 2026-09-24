using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record NewTerm
{
    /// <summary>
    /// The series name — required on every term, since two unnamed terms could not be told apart.
    /// Normalized server-side by <see cref="TermLabel"/>; the folded comparison form is derived there
    /// and is never accepted from a request.
    /// </summary>
    [Required]
    [StringLength(TermLabel.MaxLength)]
    public string? Label { get; set; }

    [Required]
    [EnumDataType(typeof(TermValueUnit))]
    public TermValueUnit ValueUnit { get; set; }

    /// <summary>
    /// Which way the money moves, from the household's perspective (issue #159). Non-nullable, so an
    /// omitted <c>direction</c> deserializes to <see cref="TermDirection.Outgoing"/> — which is
    /// exactly what omitting it means. Accepted on every term, fee and rate alike.
    ///
    /// <para>
    /// <b>This is a full-replace field on <c>PUT</c>.</b> Omitting it on an update resets the term to
    /// <c>Outgoing</c>, exactly as omitting <see cref="Label"/> or <see cref="AnchorDate"/> already
    /// clears those. Every read path returns the field, so a read-modify-write round trip carries it
    /// back unchanged.
    /// </para>
    /// </summary>
    [EnumDataType(typeof(TermDirection))]
    public TermDirection Direction { get; set; }

    /// <summary>
    /// The numeric value: required for <c>Percentage</c> and <c>Amount</c>, and must be null on
    /// <c>Text</c> and <c>DateTime</c> (issue #192). Not <c>[Required]</c>, because whether it is
    /// required depends on the unit; the permitted range does too (a fraction in [-1, 1] for
    /// percentages, >= 0 for amounts). The service enforces both.
    /// </summary>
    public decimal? Value { get; set; }

    /// <summary>
    /// A <c>Text</c> term's value: one plain line, required on <c>Text</c> and null on every other
    /// kind. Trimmed server-side; the length bound is counted after the trim, and control and bidi
    /// characters are refused (<see cref="TermTextValue"/>).
    /// </summary>
    [StringLength(TermTextValue.MaxLength)]
    public string? TextValue { get; set; }

    /// <summary>
    /// A <c>DateTime</c> term's value: an instant, required on <c>DateTime</c> and null on every other
    /// kind. Must carry an offset or <c>Z</c> — a value with neither is refused rather than read as UTC —
    /// and lie within <see cref="TermDateTimeValue.Min"/>…<see cref="TermDateTimeValue.Max"/>. No
    /// attribute: the range applies only on one kind, so the service checks it.
    /// </summary>
    public DateTime? DateTimeValue { get; set; }

    // Required for amounts (defaults to the account currency when omitted), null for percentages.
    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    /// <summary>
    /// The cadence UNIT. Refused on the two rate kinds; optional on a fee.
    /// </summary>
    [EnumDataType(typeof(Interval))]
    public Interval? Interval { get; set; }

    /// <summary>
    /// How many <see cref="Interval"/> units between charges. Accepted only when the interval is
    /// periodic (Daily/Weekly/Monthly/Annually); omitted there, it is stored as 1 — the identity
    /// cadence. Stored as null, never 1, in every non-periodic case.
    /// </summary>
    [Range(TermIntervalCount.Min, TermIntervalCount.Max)]
    public int? IntervalCount { get; set; }

    /// <summary>
    /// When the term is first actually charged, if that is not the date its price took effect.
    /// Refused on the two rate kinds. No ordering against <see cref="EffectiveFrom"/> is imposed —
    /// arrears (anchor later) and prepaid (anchor earlier) are both legitimate records.
    /// </summary>
    public DateTime? AnchorDate { get; set; }

    [Required]
    public DateTime EffectiveFrom { get; set; }

    [StringLength(512)]
    public string? Note { get; set; }
}
