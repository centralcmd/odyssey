using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Finance;

namespace Odyssey.Context;

/// <summary>
/// One currency the exchange-rate provider publishes, as last fetched (issue #351). The stored copy of the
/// provider's series list: the currency list and the sync status read only this table, so neither ever
/// waits on the provider. It is replaced whole by every sync run, by Refresh status and by Start sync's
/// live check, and kept as it is when a fetch fails.
/// </summary>
/// <remarks>
/// Stored rather than cached in memory because the API and the worker are separate processes: a run in the
/// worker could not refresh a cache the API reads. No foreign key to <see cref="Currency"/> — the provider
/// publishes currencies this instance does not hold. An empty table means "not fetched yet", which the
/// readers treat as an unknown list, never as "nothing is published".
/// </remarks>
[PrimaryKey(nameof(Provider), nameof(CurrencyCode))]
public class ExchangeRateProviderSeries
{
    public ExchangeRateProvider Provider { get; set; }

    [StringLength(3)]
    public required string CurrencyCode { get; set; }

    /// <summary>The first day the provider has a rate for the currency; null when it does not say.</summary>
    public DateOnly? FirstObservation { get; set; }

    /// <summary>The newest day it has a rate for; a series is offered while this is recent.</summary>
    public DateOnly NewestObservation { get; set; }

    /// <summary>When the list this row belongs to was fetched. Every row of one list carries the same value.</summary>
    public DateTime FetchedAtUtc { get; set; }
}
