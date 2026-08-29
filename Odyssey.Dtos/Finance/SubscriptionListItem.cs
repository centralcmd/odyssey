using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Lean list projection (issue #293 §List view): per-row scalars only — no notes — so the list
/// endpoint stays a single batched query with no N+1. <c>ExternalId</c> is included because it is
/// shown as the row's secondary text.
/// </summary>
public sealed record SubscriptionListItem
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

    public DateTime? Paused { get; set; }

    public DateTime? Archived { get; set; }
}
