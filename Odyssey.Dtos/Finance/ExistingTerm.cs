using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record ExistingTerm
{
    public required Guid TermId { get; set; }
    public required Guid AccountId { get; set; }
    public TermKind TermKind { get; set; }

    /// <summary>The series name, as the user wrote it. Null for a rate.</summary>
    [StringLength(TermLabel.MaxLength)]
    public string? Label { get; set; }

    public TermValueUnit ValueUnit { get; set; }
    public decimal Value { get; set; }

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
