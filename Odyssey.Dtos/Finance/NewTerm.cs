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
    /// exactly what omitting it means. Accepted on every contract-owned term, fee and rate alike;
    /// refused as <see cref="TermDirection.Incoming"/> on an account-owned term, where it would record
    /// a fact no surface reads.
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

    // The permitted range depends on the unit (a fraction in [-1, 1] for percentages, >= 0 for
    // amounts), so it cannot be expressed as a single [Range]; the service enforces it.
    [Required]
    public decimal Value { get; set; }

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
