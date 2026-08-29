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

    /// <summary>1 unit of From = Rate units of To. Must be greater than zero.</summary>
    [Required]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", MinimumIsExclusive = true)]
    public required decimal Rate { get; set; }

    /// <summary>Effective timestamp; defaults to <see cref="DateTime.UtcNow"/> server-side when omitted.</summary>
    public DateTime? AsOf { get; set; }
}
