using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// The currently-effective value of a single <see cref="TermKind"/> for an account — the entry with
/// the greatest <c>EffectiveFrom</c> on or before the resolution date.
/// </summary>
public sealed record CurrentAccountTerm
{
    public TermKind TermKind { get; set; }
    public TermValueUnit ValueUnit { get; set; }
    public decimal Value { get; set; }

    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    public BillingPeriod? BillingPeriod { get; set; }
    public DateTime EffectiveFrom { get; set; }
}
