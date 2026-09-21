using System.Globalization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Components;

/// <summary>
/// The one place a money figure becomes a string (Odyssey Design System · README "Numbers", and the
/// <c>moneySlot</c> / <c>money</c> / <c>signedMoney</c> / <c>moneyCompact</c> helpers in
/// <c>ui_kits/web/data.js</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Money is the amount followed by its ISO 4217 code — <c>1,234.56 USD</c>, never
/// <c>$1,234.56</c>.</b> Odyssey is multi-currency and several shipped currencies share a glyph
/// (<c>$</c> for USD and CAD, <c>kr</c> for NOK and SEK), so a symbol is ambiguous exactly where the
/// figure matters. The code also matches the <c>OdsMoneyField</c> the amount was typed into (which
/// carries its code on the right of the box) and the <c>CurrencyCode</c> the API stores. The one
/// place a symbol still belongs is the Currency admin record, where <c>Symbol</c> is a stored field
/// being edited.
/// </para>
/// <para>
/// <b>The sign slot.</b> The sign leads, then a FIGURE SPACE (U+2007 — digit-width, and not
/// collapsible HTML whitespace), then the digits, then a single ordinary space before the code:
/// <c>− 1,234.56 USD</c>, <c>+ 3,250.00 USD</c>. An <b>unsigned</b> amount carries no leading pad at
/// all — <c>1,234.56 USD</c> — because on a headline tile a padded slot reads as stray indentation,
/// and money columns align the way they always have: right-aligned, tabular figures, code trailing.
/// Negatives use a minus sign, never parentheses.
/// </para>
/// <para>
/// <b>Never glue a <c>+</c> onto a <see cref="Format"/> string</b> to make a signed figure — it lands
/// outside the slot and breaks the column. Call <see cref="Signed"/>, which fills the same slot with
/// a real plus.
/// </para>
/// <para>
/// <b>Grouping stays culture-aware</b>, deliberately. The design system hardcodes <c>en-US</c>
/// because its reference kit has no culture to read; the rule it states is about the CODE and the
/// SIGN SLOT, not about which character separates thousands. Forcing <c>en-US</c> here would regress
/// digit grouping for every non-US reader in service of a rule that never mentioned it.
/// </para>
/// </remarks>
public static class OdsMoney
{
    /// <summary>U+2007 — digit-width, and not collapsible HTML whitespace, which an ordinary space is.</summary>
    public const char FigureSpace = ' ';

    /// <summary>U+2212 — the typographic minus, which is digit-width; ASCII "-" is not.</summary>
    public const char MinusSign = '−';

    /// <summary>What a currency with no reference-data row renders to.</summary>
    public const int DefaultMinorUnits = 2;

    /// <summary>What a null figure renders as — an em dash, never "0.00", which is a real balance.</summary>
    public const string Absent = "—";

    /// <summary>The currency's own decimals (JPY renders none); 2 when the row is unknown.</summary>
    public static int MinorUnitsOf(ExistingCurrency? currency) => currency?.MinorUnits ?? DefaultMinorUnits;

    /// <summary>
    /// The sign slot: the sign plus its figure space, or nothing. Every formatter below reads this
    /// one helper, so the three cannot drift on the part a column depends on.
    /// </summary>
    /// <param name="signed">
    /// True where the figure is being PRESENTED as signed and a non-negative value should carry an
    /// explicit <c>+</c> — a net, a delta. False for an ordinary amount, where only a negative is
    /// marked.
    /// </param>
    public static string Slot(decimal value, bool signed) =>
        value < 0 ? $"{MinusSign}{FigureSpace}" : signed ? $"+{FigureSpace}" : string.Empty;

    /// <summary>An ordinary amount: <c>1,234.56 USD</c>, or <c>− 84.00 USD</c> when negative.</summary>
    public static string Format(decimal? value, string? currencyCode, int minorUnits = DefaultMinorUnits) =>
        Render(value, currencyCode, minorUnits, signed: false);

    /// <summary>An ordinary amount, taking the decimals off the currency's own row.</summary>
    public static string Format(decimal? value, string? currencyCode, ExistingCurrency? currency) =>
        Render(value, currencyCode, MinorUnitsOf(currency), signed: false);

    /// <summary>
    /// A figure presented as signed — <c>+ 3,250.00 USD</c> / <c>− 84.00 USD</c>. For a net or a
    /// delta, where "which way" is the point and a bare positive would read as an ordinary total.
    /// </summary>
    public static string Signed(decimal? value, string? currencyCode, int minorUnits = DefaultMinorUnits) =>
        Render(value, currencyCode, minorUnits, signed: true);

    /// <summary>A signed figure, taking the decimals off the currency's own row.</summary>
    public static string Signed(decimal? value, string? currencyCode, ExistingCurrency? currency) =>
        Render(value, currencyCode, MinorUnitsOf(currency), signed: true);

    /// <summary>
    /// Compact money for a chart axis: 540000 → <c>540k USD</c>, 1250000 → <c>1.25M USD</c>. The
    /// code trails here too, so an axis and the headline above it read as the same denomination.
    /// </summary>
    /// <remarks>
    /// The fractional tail is trimmed, the integer part never: a bare "strip trailing zeros" turns
    /// <c>540k</c> into <c>54k</c>, an order-of-magnitude error on an axis.
    /// </remarks>
    public static string Compact(decimal? value, string? currencyCode)
    {
        if (value is not { } amount)
            return Absent;

        var magnitude = Math.Abs(amount);
        var compact = magnitude switch
        {
            >= 1_000_000_000m => Trim(magnitude / 1_000_000_000m, magnitude % 1_000_000_000m != 0 ? 2 : 0) + "B",
            >= 1_000_000m => Trim(magnitude / 1_000_000m, magnitude % 1_000_000m != 0 ? 2 : 0) + "M",
            >= 1_000m => Trim(magnitude / 1_000m, magnitude % 1_000m != 0 ? 1 : 0) + "k",
            _ => Math.Round(magnitude).ToString("0", CultureInfo.CurrentCulture),
        };

        return $"{Slot(amount, signed: false)}{compact}{CodeSuffix(currencyCode)}";
    }

    private static string Render(decimal? value, string? currencyCode, int minorUnits, bool signed)
    {
        if (value is not { } amount)
            return Absent;

        var digits = Math.Clamp(minorUnits, 0, 28);
        var magnitude = Math.Abs(amount).ToString("N" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);
        return $"{Slot(amount, signed)}{magnitude}{CodeSuffix(currencyCode)}";
    }

    /// <summary>
    /// The trailing code, or nothing at all when the caller has no currency to name. A figure whose
    /// denomination is unknown is better bare than suffixed with a guess — the old fallback was a
    /// literal "$", which asserted USD about a value that might be anything.
    /// </summary>
    private static string CodeSuffix(string? currencyCode) =>
        string.IsNullOrWhiteSpace(currencyCode) ? string.Empty : $" {currencyCode.Trim().ToUpperInvariant()}";

    private static string Trim(decimal value, int decimals) =>
        Math.Round(value, decimals).ToString("0.##", CultureInfo.CurrentCulture);
}
