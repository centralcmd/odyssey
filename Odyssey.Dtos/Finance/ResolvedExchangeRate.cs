using System.ComponentModel.DataAnnotations;

namespace Odyssey.Dtos.Finance;

/// <summary>How the rate a conversion would use was built (issue #472 §5.3). Serialised as a number.</summary>
public enum ExchangeRateResolutionMethod
{
    /// <summary>The two currencies are the same; the rate is 1 and there are no legs.</summary>
    SameCurrency = 0,

    /// <summary>One stored <c>A → B</c> row.</summary>
    Direct = 1,

    /// <summary>One fetched <c>B → A</c> row, used as <c>1 / Rate</c>.</summary>
    Inverted = 2,

    /// <summary>Two legs, <c>A → NOK</c> then <c>NOK → B</c>, either of which may be inverted.</summary>
    ViaNok = 3,
}

/// <summary>
/// The rate <c>CurrencyConversionService</c> would use right now to convert <see cref="FromCurrencyCode"/> into
/// <see cref="ToCurrencyCode"/>, and the stored rows it was built from. Response-only.
/// </summary>
public sealed record ResolvedExchangeRate
{
    [StringLength(3)]
    public required string FromCurrencyCode { get; set; }

    [StringLength(3)]
    public required string ToCurrencyCode { get; set; }

    /// <summary>Unrounded: the value the conversion multiplies by, which can exceed 8 decimals.</summary>
    public required decimal Rate { get; set; }

    /// <summary>The effective date: the leg's for one leg, the older leg's for a hop; null for the same currency.</summary>
    public DateTime? AsOf { get; set; }

    public required ExchangeRateResolutionMethod Method { get; set; }

    public required IReadOnlyList<ResolvedExchangeRateLeg> Legs { get; set; }
}

/// <summary>
/// One stored row a <see cref="ResolvedExchangeRate"/> used, shown in the LOGICAL direction with the rate actually
/// multiplied in. <see cref="ExchangeRateId"/> names the stored row, whose own pair is the reverse when
/// <see cref="Inverted"/> is true.
/// </summary>
public sealed record ResolvedExchangeRateLeg
{
    public required Guid ExchangeRateId { get; set; }

    [StringLength(3)]
    public required string FromCurrencyCode { get; set; }

    [StringLength(3)]
    public required string ToCurrencyCode { get; set; }

    public required decimal Rate { get; set; }

    public required DateTime AsOf { get; set; }

    public required ExchangeRateSource Source { get; set; }

    public required bool Inverted { get; set; }
}

/// <summary>
/// The <c>from</c>/<c>to</c> query string of <c>GET /api/exchange-rates/latest</c> and <c>/resolved</c>. A
/// <c>sealed class</c> like the other query-string binding models. The pattern runs on the raw value and ends in
/// <c>\z</c>, so a padded, newline-terminated or non-letter code is a <c>400</c> before anything echoes it.
/// </summary>
public sealed class CurrencyPairQuery
{
    public const string CodePattern = @"^[A-Za-z]{3}\z";

    [Required]
    [RegularExpression(CodePattern, ErrorMessage = "A currency code is exactly three letters.")]
    public string? From { get; set; }

    [Required]
    [RegularExpression(CodePattern, ErrorMessage = "A currency code is exactly three letters.")]
    public string? To { get; set; }
}
