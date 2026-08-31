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

    /// <summary>
    /// The next charge on or after today, derived from <see cref="FirstBillingDate"/> + the interval
    /// (display only — nothing is scheduled or stored). Null when there is no next charge: the term
    /// has lapsed, the next occurrence would fall past <see cref="EndDate"/>, or the subscription is
    /// paused or archived. The emptiness is part of the derivation, not a missing value — the same
    /// rule <c>SubscriptionSummary.UpcomingRenewals</c> applies, so a client never has to re-derive
    /// it (and the two can never disagree).
    /// </summary>
    public DateOnly? NextBillingDate { get; set; }

    public required DateTime CreatedAtUtc { get; set; }
}
