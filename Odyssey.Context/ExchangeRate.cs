using Microsoft.EntityFrameworkCore;
using Odyssey.Dtos.Finance;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odyssey.Context;

/// <summary>
/// A timestamped exchange rate. Conversions use the latest rate — the newest
/// (<see cref="AsOf"/>, <see cref="UpdatedAt"/> ?? <see cref="CreatedAt"/>) — for a given
/// (<see cref="FromCurrencyCode"/>, <see cref="ToCurrencyCode"/>) pair. A record's currency pair is
/// immutable once created; <see cref="Rate"/>/<see cref="AsOf"/> of a manual row can be corrected in
/// place (see <c>ExchangeRateService.Update</c>), which is why the tiebreak folds in
/// <see cref="UpdatedAt"/> — otherwise a correction could lose "latest" to an unrelated row inserted
/// after it.
/// </summary>
/// <remarks>
/// <para>
/// The unique <c>(From, To, AsOf, Provider)</c> index makes a fetched row idempotent (issue #349 §4):
/// MariaDB treats <c>NULL</c>s as distinct, so manual rows — <see cref="Provider"/> <c>NULL</c> — are
/// unaffected. Its <c>(From, To, AsOf)</c> prefix also serves the "latest rate" lookup the old
/// non-unique index existed for. The <c>(AsOf)</c> index serves the list's default sort.
/// </para>
/// </remarks>
[Index(nameof(FromCurrencyCode), nameof(ToCurrencyCode), nameof(AsOf), nameof(Provider), IsUnique = true)]
[Index(nameof(AsOf))]
public class ExchangeRate
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid ExchangeRateId { get; set; }

    /// <summary>The currency being converted from. FK to <see cref="Currency.CurrencyCode"/>.</summary>
    [StringLength(3)]
    [Required]
    public required string FromCurrencyCode { get; set; }

    /// <summary>The currency being converted to. FK to <see cref="Currency.CurrencyCode"/>.</summary>
    [StringLength(3)]
    [Required]
    public required string ToCurrencyCode { get; set; }

    /// <summary>1 unit of <see cref="FromCurrencyCode"/> equals <see cref="Rate"/> units of <see cref="ToCurrencyCode"/>. Must be &gt; 0.</summary>
    [Required]
    public required decimal Rate { get; set; }

    /// <summary>The effective timestamp used to pick the latest rate for a pair.</summary>
    [Required]
    public required DateTime AsOf { get; set; }

    /// <summary>Server-set record insertion time (audit trail).</summary>
    [Required]
    public DateTime CreatedAt { get; set; }

    /// <summary>Server-set time of the last in-place Rate/AsOf correction; null if never corrected.</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// The provider a fetched row came from; <c>NULL</c> means the row was entered by hand. There is no
    /// separate source column — it would encode the same fact twice (issue #349 §4).
    /// </summary>
    public ExchangeRateProvider? Provider { get; set; }
}
