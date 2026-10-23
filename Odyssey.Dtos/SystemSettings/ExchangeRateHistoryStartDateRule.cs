using System.Globalization;

namespace Odyssey.Dtos;

/// <summary>
/// The shape rule for the global exchange-rate history start date (issue #349 §8). Shared by the server's
/// write and read paths and the client's settings page, so all three name one delegate.
///
/// <para>
/// <strong>The rule is split.</strong> The format and the <see cref="Floor"/> are static and live here;
/// "on or before today" needs a clock, so it is checked only on the server write path against the
/// injected <c>TimeProvider</c>. Empty is legal and means "1 January of the current UTC year", resolved
/// at run time — a migration seeds a compile-time value and cannot say "this year".
/// </para>
/// </summary>
public static class ExchangeRateHistoryStartDateRule
{
    /// <summary><c>yyyy-MM-dd</c>.</summary>
    public const int MaxLength = 10;

    public const string Format = "yyyy-MM-dd";

    /// <summary>The earliest start date accepted; no supported provider publishes anything older.</summary>
    public static readonly DateOnly Floor = new(1980, 1, 1);

    /// <summary>Returns an error message, or null when <paramref name="value"/> is acceptable.</summary>
    public static string? Validate(string value)
    {
        if (!TryParse(value, out var date))
        {
            return "must be a date in the form yyyy-MM-dd.";
        }

        return date < Floor
            ? $"must be on or after {Floor.ToString(Format, CultureInfo.InvariantCulture)}."
            : null;
    }

    public static bool TryParse(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value?.Trim(), Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>The start used when no date is set: 1 January of <paramref name="todayUtc"/>'s year.</summary>
    public static DateOnly DynamicDefault(DateOnly todayUtc) => new(todayUtc.Year, 1, 1);
}
