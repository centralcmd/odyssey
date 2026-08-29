using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record ExistingSubscription
{
    public required Guid SubscriptionId { get; set; }

    [StringLength(128)]
    public required string Name { get; set; }

    [StringLength(128)]
    public string? ExternalId { get; set; }

    public SubscriptionContactReference? Contact { get; set; }

    public required DateOnly StartDate { get; set; }

    public DateOnly? EndDate { get; set; }

    public required decimal Amount { get; set; }

    [StringLength(3)]
    public required string CurrencyCode { get; set; }

    public BillingInterval Interval { get; set; }

    public int IntervalCount { get; set; }

    public required DateOnly FirstBillingDate { get; set; }

    [StringLength(1024)]
    public string? Notes { get; set; }

    public DateTime? Paused { get; set; }

    public DateTime? Archived { get; set; }

    public required DateTime CreatedAtUtc { get; set; }
}
