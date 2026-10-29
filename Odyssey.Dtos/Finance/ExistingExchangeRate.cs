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

    /// <summary>The provider a fetched rate came from; null for a hand-entered rate.</summary>
    public ExchangeRateProvider? Provider { get; set; }

    /// <summary>Derived from <see cref="Provider"/>: a row with no provider was entered by hand.</summary>
    public ExchangeRateSource Source { get; set; }

    /// <summary>Computed by the same server rule the list's <c>status</c> filter uses (issue #349 §5.6).</summary>
    public ExchangeRateStatus Status { get; set; }
}
