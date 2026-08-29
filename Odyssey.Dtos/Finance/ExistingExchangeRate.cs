using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record ExistingExchangeRate
{
    public required Guid ExchangeRateId { get; set; }

    [StringLength(3)]
    public required string FromCurrencyCode { get; set; }

    [StringLength(3)]
    public required string ToCurrencyCode { get; set; }

    public required decimal Rate { get; set; }

    public required DateTime AsOf { get; set; }

    public required DateTime CreatedAt { get; set; }

    /// <summary>When this rate's Rate/AsOf was last corrected in place; null if never corrected.</summary>
    public DateTime? UpdatedAt { get; set; }
}
