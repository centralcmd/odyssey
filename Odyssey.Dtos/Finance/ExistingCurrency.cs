using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record ExistingCurrency
{
    [StringLength(3)]
    public required string CurrencyCode { get; set; }

    [StringLength(64)]
    public required string Name { get; set; }

    [Range(0, 12)]
    public int MinorUnits { get; set; }

    [StringLength(8)]
    public required string Symbol { get; set; }

    public DateTime? Archived { get; set; }

    /// <summary>
    /// The currency's exchange-rate sync projection. Filled only for a caller holding
    /// <c>system-settings.read</c>, and never for an archived currency, which the sync does not cover.
    /// </summary>
    public ExistingExchangeRateSyncCurrency? Sync { get; set; }

    /// <summary>
    /// The derived <see cref="CurrencyStatus"/>, for every caller. Set by the list endpoint; null on the
    /// single-currency reads and writes, which do not compute the sync projection it is derived from.
    /// </summary>
    public CurrencyStatus? Status { get; set; }
}
