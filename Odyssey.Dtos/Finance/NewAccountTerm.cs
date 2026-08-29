using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record NewAccountTerm
{
    [Required]
    [EnumDataType(typeof(TermKind))]
    public TermKind TermKind { get; set; }

    [Required]
    [EnumDataType(typeof(TermValueUnit))]
    public TermValueUnit ValueUnit { get; set; }

    // The permitted range depends on the unit (a fraction in [-1, 1] for percentages, >= 0 for
    // amounts), so it cannot be expressed as a single [Range]; the service enforces it.
    [Required]
    public decimal Value { get; set; }

    // Required for amounts (defaults to the account currency when omitted), null for percentages.
    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    [EnumDataType(typeof(BillingPeriod))]
    public BillingPeriod? BillingPeriod { get; set; }

    [Required]
    public DateTime EffectiveFrom { get; set; }

    [StringLength(512)]
    public string? Note { get; set; }
}
