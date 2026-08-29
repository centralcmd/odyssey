using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record UpdateSubscription
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

    /// <summary>
    /// Pause (flag as temporarily not billing) when true, or resume when false. Paused subscriptions
    /// stay visible in the default list with a "Paused" badge. The service owns the timestamp
    /// (via <c>TimeProvider</c>); a repeated <c>true</c> preserves the original pause time. Independent
    /// of <see cref="Archived"/>.
    /// </summary>
    public bool Paused { get; set; }

    /// <summary>
    /// Archive (soft-hide) the subscription when true, or unarchive when false. Archiving keeps the
    /// record but drops it from the default (Active) list; deletion (<c>DELETE</c>) is permanent.
    /// Independent of <see cref="Paused"/>.
    /// </summary>
    public bool Archived { get; set; }
}
