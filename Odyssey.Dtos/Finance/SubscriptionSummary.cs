namespace Odyssey.Dtos.Finance;

/// <summary>Subscription counts by lifecycle state (issue #293 summary follow-up). Active/Paused/Ended
/// are the live buckets (non-archived); <see cref="Ended"/> is a DERIVED terminal state (its
/// <c>EndDate</c> has lapsed on/before today) that supersedes Paused; Archived is counted separately.</summary>
public sealed record SubscriptionStatusCounts
{
    public int Active { get; set; }

    public int Paused { get; set; }

    /// <summary>Live subscriptions whose term has lapsed (<c>EndDate</c> ≤ today). Supersedes Paused.</summary>
    public int Ended { get; set; }

    public int Archived { get; set; }
}

/// <summary>A per-interval count used by the "By interval" breakdown (live subscriptions only).</summary>
public sealed record SubscriptionIntervalCount
{
    public BillingInterval Interval { get; set; }

    public int Count { get; set; }
}

/// <summary>The run-rate contribution of a single billing currency: the cadence-normalized monthly and
/// yearly spend, in that currency (never converted here — the per-currency figures stay untouched).</summary>
public sealed record SubscriptionRunRateRow
{
    public required string CurrencyCode { get; set; }

    public decimal Monthly { get; set; }

    public decimal Yearly { get; set; }

    public int Count { get; set; }
}

/// <summary>The single biggest recurring cost, compared on a monthly-equivalent basis in the base
/// currency (falls back to the raw monthly-equivalent when no FX rate is available).</summary>
public sealed record SubscriptionRunRateDriver
{
    public required Guid SubscriptionId { get; set; }

    public required string Name { get; set; }

    public decimal Amount { get; set; }

    public required string CurrencyCode { get; set; }

    public BillingInterval Interval { get; set; }
}

/// <summary>
/// The estimated recurring spend across all live (non-archived, non-paused, not-yet-ended)
/// subscriptions. Cadence is normalized to a monthly/yearly equivalent; per-currency figures are kept
/// verbatim (<see cref="Rows"/>), and a converted blended total is computed against
/// <see cref="BaseCurrency"/> where an exchange rate exists — currencies with no direct rate are
/// listed in <see cref="ExcludedCurrencies"/> and left out of the converted totals.
/// </summary>
public sealed record SubscriptionRunRate
{
    public required string BaseCurrency { get; set; }

    /// <summary>Blended monthly spend converted to <see cref="BaseCurrency"/>; null when nothing is convertible.</summary>
    public decimal? ConvertedMonthly { get; set; }

    /// <summary>Blended yearly spend converted to <see cref="BaseCurrency"/>; null when nothing is convertible.</summary>
    public decimal? ConvertedYearly { get; set; }

    public List<SubscriptionRunRateRow> Rows { get; set; } = new();

    /// <summary>Currencies present in the run-rate that have no rate to <see cref="BaseCurrency"/> (excluded from the converted totals).</summary>
    public List<string> ExcludedCurrencies { get; set; } = new();

    public SubscriptionRunRateDriver? TopDriver { get; set; }
}

/// <summary>A derived upcoming charge: the next billing date computed from the anchor + interval
/// (display only — nothing is scheduled or stored), within the summary's look-ahead window.</summary>
public sealed record SubscriptionRenewal
{
    public required Guid SubscriptionId { get; set; }

    public required string Name { get; set; }

    public decimal Amount { get; set; }

    public required string CurrencyCode { get; set; }

    public BillingInterval Interval { get; set; }

    public required DateOnly NextBillingDate { get; set; }

    /// <summary>Whole days from today to <see cref="NextBillingDate"/> (0 = today).</summary>
    public int DaysUntil { get; set; }
}

/// <summary>
/// Server-computed rollup for the subscriptions page header (issue #293 summary follow-up): status +
/// interval counts, the multi-currency run-rate, and the derived upcoming renewals. All figures are
/// display-only — the anchor is never advanced or persisted (no scheduler).
/// </summary>
public sealed record SubscriptionSummary
{
    /// <summary>Live subscriptions (non-archived): <see cref="SubscriptionStatusCounts.Active"/> + <see cref="SubscriptionStatusCounts.Paused"/> + <see cref="SubscriptionStatusCounts.Ended"/>.</summary>
    public int Total { get; set; }

    public required SubscriptionStatusCounts CountsByStatus { get; set; }

    public List<SubscriptionIntervalCount> CountsByInterval { get; set; } = new();

    public required SubscriptionRunRate RunRate { get; set; }

    public List<SubscriptionRenewal> UpcomingRenewals { get; set; } = new();
}
