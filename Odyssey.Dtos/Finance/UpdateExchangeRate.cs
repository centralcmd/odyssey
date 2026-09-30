using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Corrects an existing exchange-rate record's Rate and AsOf. The currency pair is the record's
/// identity and is never accepted here — it stays locked to whatever the record was created with.
/// </summary>
public sealed record UpdateExchangeRate
{
    /// <summary>1 unit of From = Rate units of To. Bounded by the <c>decimal(18,8)</c> column (<see cref="MoneyBounds"/>).</summary>
    [Required]
    [Range(typeof(decimal), MoneyBounds.ExchangeRateMin, MoneyBounds.ExchangeRateMax, ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public required decimal Rate { get; set; }

    [Required]
    public required DateTime AsOf { get; set; }
}
