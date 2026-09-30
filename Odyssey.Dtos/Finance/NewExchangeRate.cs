using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

public sealed record NewExchangeRate
{
    [Required]
    [StringLength(3)]
    public required string FromCurrencyCode { get; set; }

    [Required]
    [StringLength(3)]
    public required string ToCurrencyCode { get; set; }

    /// <summary>1 unit of From = Rate units of To. Bounded by the <c>decimal(18,8)</c> column (<see cref="MoneyBounds"/>).</summary>
    [Required]
    [Range(typeof(decimal), MoneyBounds.ExchangeRateMin, MoneyBounds.ExchangeRateMax, ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public required decimal Rate { get; set; }

    /// <summary>Effective timestamp; defaults to <see cref="DateTime.UtcNow"/> server-side when omitted.</summary>
    public DateTime? AsOf { get; set; }
}
