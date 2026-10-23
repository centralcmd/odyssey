using System.ComponentModel.DataAnnotations;

namespace Odyssey.Context;

/// <summary>
/// Whether one currency is synced from the exchange-rate provider, and from when (issue #349 §4). A
/// separate table rather than a column on <see cref="Currency"/>, deliberately: a currency is written
/// under <c>currencies.update</c>, which Owner holds, while sync control is Admin-only. There are no seed
/// rows — an absent row means "off".
/// </summary>
public class ExchangeRateSyncCurrency
{
    /// <summary>PK and FK to <see cref="Currency.CurrencyCode"/>, cascading with the currency.</summary>
    [Key]
    [StringLength(3)]
    public required string CurrencyCode { get; set; }

    public bool Enabled { get; set; }

    /// <summary>The per-currency start-date override; null falls back to the global setting.</summary>
    public DateOnly? HistoryStartDate { get; set; }

    /// <summary>
    /// The contiguous backfill watermark: every business day from here to the newest fetched date has been
    /// requested successfully. Backfill is complete once it is on or before the effective start date.
    /// </summary>
    public DateOnly? BackfilledFrom { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string? UpdatedByUserId { get; set; }
}
