using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// The currently-effective value of a single term series for an account — the entry with the greatest
/// <c>EffectiveFrom</c> on or before the resolution date. A series is a <see cref="TermKind"/> plus a
/// <see cref="Label"/>, so a kind carrying several named fees resolves one entry per name.
/// </summary>
public sealed record CurrentAccountTerm
{
    public TermKind TermKind { get; set; }
    public TermValueUnit ValueUnit { get; set; }
    public decimal Value { get; set; }

    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    public BillingPeriod? BillingPeriod { get; set; }

    /// <summary>Names this term's series within its kind; null is the kind's unnamed series.</summary>
    [StringLength(TermLabel.MaxLength)]
    public string? Label { get; set; }

    public DateTime EffectiveFrom { get; set; }
}
