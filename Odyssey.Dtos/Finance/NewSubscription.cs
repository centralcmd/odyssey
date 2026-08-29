using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record NewSubscription
{
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public required string Name { get; set; }

    [StringLength(128)]
    public string? ExternalId { get; set; }

    public Guid? ContactId { get; set; }

    [Required]
    public required DateOnly StartDate { get; set; }

    public DateOnly? EndDate { get; set; }

    [Required]
    [Range(0, 1_000_000_000)]
    public required decimal Amount { get; set; }

    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string CurrencyCode { get; set; } = "USD";

    [Required]
    [EnumDataType(typeof(BillingInterval))]
    public BillingInterval Interval { get; set; } = BillingInterval.Monthly;

    /// <summary>Cadence multiplier: how many <see cref="Interval"/> units between billings (1 = every unit, 3 = every 3 months).</summary>
    [Required]
    [Range(1, 1000)]
    public int IntervalCount { get; set; } = 1;

    [Required]
    public required DateOnly FirstBillingDate { get; set; }

    [StringLength(1024)]
    public string? Notes { get; set; }
}
