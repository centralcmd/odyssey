using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record ExistingAccountTerm
{
    public required Guid AccountTermId { get; set; }
    public required Guid AccountId { get; set; }
    public TermKind TermKind { get; set; }
    public TermValueUnit ValueUnit { get; set; }
    public decimal Value { get; set; }

    [StringLength(3)]
    public string? CurrencyCode { get; set; }

    public BillingPeriod? BillingPeriod { get; set; }
    public DateTime EffectiveFrom { get; set; }

    [StringLength(512)]
    public string? Note { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
