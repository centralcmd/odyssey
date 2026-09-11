using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// The currently-effective value of a single series on an account — the entry with the greatest
/// <c>EffectiveFrom</c> on or before the resolution date within its <c>(TermKind, Label)</c> series.
/// One kind can therefore contribute several entries, one per label.
/// </summary>
public sealed record CurrentAccountTerm
{
    public TermKind TermKind { get; set; }

    /// <summary>The series name, as the user wrote it. Null for a rate.</summary>
    [StringLength(TermLabel.MaxLength)]
    public string? Label { get; set; }
    public TermValueUnit ValueUnit { get; set; }
    public decimal Value { get; set; }

    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    public BillingPeriod? BillingPeriod { get; set; }
    public DateTime EffectiveFrom { get; set; }
}
