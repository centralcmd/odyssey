using System.Globalization;
using System.Text.RegularExpressions;

namespace Odyssey.Dtos.Finance;

/// <summary>
/// The identifier rules for an instrument and its source symbols (issue #446 §8.7). Declared once here, in the
/// zero-reference leaf assembly the WASM client also reaches, so the server's validators and the client's form
/// checks run the same code. A source-lint fails on a second copy.
/// </summary>
public static class InstrumentIdentifierRules
{
    /// <summary>A ticker as people write it: letters, digits, dots, hyphens and spaces, starting with a letter or digit.</summary>
    public const string SymbolPattern = @"^[A-Za-z0-9][A-Za-z0-9.\- ]*$";

    /// <summary>An ISO 10383 Market Identifier Code: four letters or digits.</summary>
    public const string MicPattern = "^[A-Za-z0-9]{4}$";

    /// <summary>An ISO 6166 ISIN's shape: 2 country letters, 9 alphanumerics, 1 check digit.</summary>
    public const string IsinPattern = "^[A-Za-z]{2}[A-Za-z0-9]{9}[0-9]$";

    /// <summary>A source's own symbol (e.g. <c>EQNR.OL</c>, <c>NO0010765134.EUFUND</c>).</summary>
    public const string SourceSymbolPattern = @"^[A-Za-z0-9][A-Za-z0-9._\-]*$";

    /// <summary>A code-declared price source key.</summary>
    public const string SourceKeyPattern = "^[a-z0-9-]{1,32}$";

    public const int SymbolMaxLength = 32;
    public const int MicLength = 4;
    public const int IsinLength = 12;
    public const int NameMaxLength = 256;
    public const int SourceSymbolMaxLength = 64;
    public const int SourceKeyMaxLength = 32;
    public const int ListingKeyMaxLength = 64;

    private static readonly Regex IsinRegex = new(IsinPattern, RegexOptions.CultureInvariant);

    /// <summary>Trims and upper-cases a symbol, MIC or ISIN; <c>null</c> and blank become <c>null</c>.</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToUpperInvariant();
    }

    /// <summary>
    /// Whether an ISIN's last character is the right check digit: letters expand to two digits (A = 10 … Z = 35),
    /// then the Luhn algorithm runs over the expanded string. A value of the wrong shape is never valid.
    /// </summary>
    public static bool IsValidIsinCheckDigit(string? isin)
    {
        var value = Normalize(isin);
        if (value is null || !IsinRegex.IsMatch(value))
        {
            return false;
        }

        var digits = new System.Text.StringBuilder(24);
        foreach (var ch in value)
        {
            digits.Append(char.IsDigit(ch)
                ? ch.ToString()
                : (ch - 'A' + 10).ToString(CultureInfo.InvariantCulture));
        }

        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }

    /// <summary>
    /// Whether a source symbol is <c>.</c> or <c>..</c> or contains a <c>..</c> segment. <c>System.Uri</c> collapses
    /// dot segments even after escaping, so such a symbol could change the request path and is refused everywhere.
    /// </summary>
    public static bool HasDotSegment(string? symbol)
    {
        if (string.IsNullOrEmpty(symbol))
        {
            return false;
        }

        return symbol is "." or ".." || symbol.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>
    /// The instrument's identity for duplicate detection: <c>SYMBOL@MIC</c>, or <c>ISIN:&lt;isin&gt;:&lt;currency&gt;</c>
    /// when there is no MIC. Computed from normalised values, never by the database.
    /// </summary>
    public static string ListingKey(string symbol, string? micCode, string? isin, string currencyCode)
    {
        var mic = Normalize(micCode);
        return mic is not null
            ? $"{Normalize(symbol)}@{mic}"
            : $"ISIN:{Normalize(isin)}:{currencyCode.Trim().ToUpperInvariant()}";
    }
}

/// <summary>The price-value rules (issue #446 §8.7), shared by the manual DTO, the fetched-batch check and the client.</summary>
public static class InstrumentPriceRules
{
    /// <summary>The scale of the close column, and the most decimals a close may carry.</summary>
    public const int MaxDecimals = 8;

    /// <summary>
    /// Whether <paramref name="value"/> needs at most <see cref="MaxDecimals"/> decimal places. Value-based, not
    /// scale-based: <c>System.Text.Json</c> keeps the scale as written, so <c>274.500000000</c> (scale 9) has one
    /// significant decimal and passes.
    /// </summary>
    public static bool HasAtMostDecimals(decimal value) => decimal.Round(value, MaxDecimals) == value;
}

/// <summary>The binding limits (issue #446 §4.5): the one constant behind the CHECK, the attribute and the response field.</summary>
public static class InstrumentPriceBindingLimits
{
    public const int MaxBindingsPerInstrument = 9;
}

/// <summary>
/// Today's date in Europe/Oslo (issue #446 §8.7). Throws <see cref="TimeZoneNotFoundException"/> when the zone
/// cannot be resolved — it never falls back to UTC, which would be a day off around midnight. Both hosts assert
/// the zone at startup (<c>TimeZoneDataGuard</c>); the WASM client catches the exception and degrades.
/// </summary>
public static class OsloDate
{
    public const string TimeZoneId = "Europe/Oslo";

    public static TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    public static DateOnly Today(TimeProvider timeProvider) => FromUtc(timeProvider.GetUtcNow().UtcDateTime);

    /// <summary>The Oslo calendar date of a UTC instant.</summary>
    public static DateOnly FromUtc(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone));

    /// <summary>The UTC instant at which <paramref name="date"/> begins in Oslo.</summary>
    public static DateTime StartOfDayUtc(DateOnly date) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), Zone);
}
