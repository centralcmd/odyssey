namespace Odyssey.Dtos;

/// <summary>
/// The write-side bounds of every money amount and exchange rate a request can carry, derived from the
/// column each one is stored in (issue #240). One declaration, named by every <c>[Range]</c> that
/// guards such a column, so a request DTO can never accept a value its column cannot hold.
/// </summary>
/// <remarks>
/// <para>
/// Without these bounds a value outside the column's precision reached MariaDB and failed there
/// (error 1264, out of range) as a <c>500</c>, and a rate below half the rate column's smallest step
/// passed <c>Rate &gt; 0</c> and was stored as <c>0.00000000</c> — silently zeroing every conversion
/// through it. Both are compile-time bounds, so they belong in <c>[Range]</c> (CLAUDE.md), where model
/// validation turns them into a <c>400</c> before the service runs.
/// </para>
/// <para>
/// The limits are strings because <c>[Range(typeof(decimal), …)]</c> takes its limits that way — a
/// <c>decimal</c> is not a valid attribute argument. Every <c>[Range]</c> naming them must set
/// <c>ParseLimitsInInvariantCulture</c> and <c>ConvertValueInInvariantCulture</c>: the limits carry a
/// <c>.</c> separator, which a comma-decimal culture would otherwise misparse.
/// </para>
/// <para>
/// The precision/scale pairs restate the EF model's <c>[Precision]</c>/<c>HasPrecision</c>; a guard
/// test in <c>Odyssey.Api.Tests</c> reads the model and fails if either side moves without the other.
/// </para>
/// </remarks>
public static class MoneyBounds
{
    /// <summary>Precision of every money-amount column: <c>decimal(18,6)</c>.</summary>
    public const int AmountPrecision = 18;

    /// <summary>Scale of every money-amount column: <c>decimal(18,6)</c>.</summary>
    public const int AmountScale = 6;

    /// <summary>The largest magnitude a <c>decimal(18,6)</c> column holds, negated.</summary>
    public const string AmountMin = "-999999999999.999999";

    /// <summary>The largest magnitude a <c>decimal(18,6)</c> column holds.</summary>
    public const string AmountMax = "999999999999.999999";

    /// <summary>Precision of the exchange-rate column: <c>decimal(18,8)</c>.</summary>
    public const int ExchangeRatePrecision = 18;

    /// <summary>Scale of the exchange-rate column: <c>decimal(18,8)</c>.</summary>
    public const int ExchangeRateScale = 8;

    /// <summary>
    /// The smallest positive rate a <c>decimal(18,8)</c> column holds. Anything smaller is stored as
    /// zero (or rounded up to this), so it is refused rather than accepted as a different number.
    /// </summary>
    public const string ExchangeRateMin = "0.00000001";

    /// <summary>The largest rate a <c>decimal(18,8)</c> column holds.</summary>
    public const string ExchangeRateMax = "9999999999.99999999";
}
