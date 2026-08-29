using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// Corrects an existing exchange-rate record's Rate and AsOf. The currency pair is the record's
/// identity and is never accepted here — it stays locked to whatever the record was created with.
/// </summary>
public sealed record UpdateExchangeRate
{
    /// <summary>1 unit of From = Rate units of To. Must be greater than zero.</summary>
    [Required]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", MinimumIsExclusive = true)]
    public required decimal Rate { get; set; }

    [Required]
    public required DateTime AsOf { get; set; }
}
