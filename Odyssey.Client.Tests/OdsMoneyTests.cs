using System.Globalization;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The one money formatter (Odyssey Design System · README "Numbers"). Every finance surface routes
/// through it, so the rules it encodes are asserted here rather than re-asserted per page.
/// </summary>
/// <remarks>
/// The sign slot is the part a column depends on and the part an eye cannot check: a figure space
/// (U+2007) and an ordinary space render almost identically, and only the figure space is
/// digit-width and non-collapsing in HTML. So the characters are asserted by codepoint, never by
/// pasting a glyph into an expectation.
/// </remarks>
public class OdsMoneyTests
{
    private static ExistingCurrency Currency(string code, string symbol, int minorUnits) =>
        new() { CurrencyCode = code, Name = code, Symbol = symbol, MinorUnits = minorUnits };

    // ── The code, never a symbol ──

    /// <summary>
    /// Money is the amount followed by its ISO 4217 code. Several shipped currencies share a glyph
    /// ("$" for USD and CAD, "kr" for NOK and SEK), so a symbol is ambiguous exactly where the figure
    /// matters — and the code matches the OdsMoneyField the amount was typed into.
    /// </summary>
    [Fact]
    public void Format_PutsTheIsoCodeAfterTheFigure()
    {
        Assert.EndsWith(" NOK", OdsMoney.Format(1234.5m, "NOK", Currency("NOK", "kr", 2)), StringComparison.Ordinal);
    }

    /// <summary>The currency's own symbol is not rendered even when the reference row carries one.</summary>
    [Fact]
    public void Format_NeverRendersTheCurrencysSymbol()
    {
        var rendered = OdsMoney.Format(1234.5m, "NOK", Currency("NOK", "kr", 2));

        Assert.DoesNotContain("kr", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nok", " NOK")]
    [InlineData("  usd  ", " USD")]
    public void Format_NormalisesTheCode(string code, string expectedSuffix)
    {
        Assert.EndsWith(expectedSuffix, OdsMoney.Format(10m, code), StringComparison.Ordinal);
    }

    /// <summary>
    /// A naive cross-currency aggregate has no denomination to name, so it carries no code. The old
    /// behaviour was a generic "$", which asserted USD about a figure that might be in anything.
    /// </summary>
    [Fact]
    public void Format_WithNoCurrency_CarriesNoCodeAndNoTrailingSpace()
    {
        var rendered = OdsMoney.Format(1234.5m, currencyCode: null);

        Assert.DoesNotContain("$", rendered, StringComparison.Ordinal);
        Assert.Equal(rendered.TrimEnd(), rendered);
    }

    // ── Decimals ──

    /// <summary>JPY has no minor units — two decimals would invent precision the currency lacks.</summary>
    [Fact]
    public void Format_HonoursAZeroMinorUnitCurrency()
    {
        var rendered = OdsMoney.Format(1234.56m, "JPY", Currency("JPY", "¥", 0));

        // Asserted through the culture's own separator rather than a literal ".", so the test pins the
        // rule and not the host's culture.
        Assert.DoesNotContain(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void MinorUnitsOf_WithNoRow_IsTwo()
    {
        Assert.Equal(2, OdsMoney.MinorUnitsOf(null));
        Assert.Equal(OdsMoney.DefaultMinorUnits, OdsMoney.MinorUnitsOf(null));
    }

    // ── The sign slot ──

    /// <summary>
    /// A negative leads with the TYPOGRAPHIC minus (U+2212), which is digit-width, then a figure
    /// space. ASCII "-" is narrower and would pull the digits out of a money column.
    /// </summary>
    [Fact]
    public void Format_ANegative_LeadsWithAMinusAndAFigureSpace()
    {
        var rendered = OdsMoney.Format(-84m, "USD");

        Assert.StartsWith($"{OdsMoney.MinusSign}{OdsMoney.FigureSpace}", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("-", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("(", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// An UNSIGNED amount carries no leading pad at all. An empty slot was tried and reads as stray
    /// indentation on a headline tile, where there is no column to align to; money columns align the
    /// way they always have — right-aligned, tabular figures, code trailing.
    /// </summary>
    [Fact]
    public void Format_APositive_CarriesNoLeadingPad()
    {
        var rendered = OdsMoney.Format(84m, "USD");

        Assert.DoesNotContain(OdsMoney.FigureSpace, rendered);
        Assert.StartsWith("8", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// A figure PRESENTED as signed — a net, a delta — fills the same slot with a real plus, so it
    /// lines up with the grosses above it. Gluing a "+" onto a Format string instead lands the sign
    /// outside the slot and breaks the column.
    /// </summary>
    [Fact]
    public void Signed_APositive_FillsTheSlotWithAPlus()
    {
        Assert.StartsWith($"+{OdsMoney.FigureSpace}", OdsMoney.Signed(3250m, "USD"), StringComparison.Ordinal);
    }

    /// <summary>Signed and unsigned negatives are the same string: only the positive case differs.</summary>
    [Fact]
    public void Signed_ANegative_MatchesTheUnsignedForm()
    {
        Assert.Equal(OdsMoney.Format(-84m, "USD"), OdsMoney.Signed(-84m, "USD"));
    }

    [Fact]
    public void Slot_IsTheOneDefinitionOfTheLead()
    {
        Assert.Equal($"{OdsMoney.MinusSign}{OdsMoney.FigureSpace}", OdsMoney.Slot(-1m, signed: false));
        Assert.Equal($"{OdsMoney.MinusSign}{OdsMoney.FigureSpace}", OdsMoney.Slot(-1m, signed: true));
        Assert.Equal(string.Empty, OdsMoney.Slot(1m, signed: false));
        Assert.Equal($"+{OdsMoney.FigureSpace}", OdsMoney.Slot(1m, signed: true));
        // Zero is not negative, so it takes the positive branch in both modes.
        Assert.Equal(string.Empty, OdsMoney.Slot(0m, signed: false));
    }

    // ── Absent ──

    [Fact]
    public void ANullFigure_IsAnEmDashAndNeverZero()
    {
        Assert.Equal(OdsMoney.Absent, OdsMoney.Format(null, "USD"));
        Assert.Equal(OdsMoney.Absent, OdsMoney.Signed(null, "USD"));
        Assert.Equal(OdsMoney.Absent, OdsMoney.Compact(null, "USD"));
    }

    // ── Compact ──

    [Theory]
    [InlineData(540_000, "540k USD")]
    [InlineData(1_250_000, "1.25M USD")]
    [InlineData(2_000_000_000, "2B USD")]
    [InlineData(980, "980 USD")]
    public void Compact_AbbreviatesWithTheCodeTrailing(decimal value, string expected)
    {
        Assert.Equal(expected, OdsMoney.Compact(value, "USD"));
    }

    /// <summary>
    /// Only a FRACTIONAL tail is trimmed. A bare "strip trailing zeros" eats the zeros of an integer
    /// and turns 540k into 54k — an order-of-magnitude error on an axis.
    /// </summary>
    [Fact]
    public void Compact_TrimsOnlyTheFractionalTail()
    {
        Assert.Equal("540k USD", OdsMoney.Compact(540_000m, "USD"));
        Assert.Equal("54k USD", OdsMoney.Compact(54_000m, "USD"));
    }

    [Fact]
    public void Compact_UsesTheSameSignSlot()
    {
        Assert.StartsWith($"{OdsMoney.MinusSign}{OdsMoney.FigureSpace}", OdsMoney.Compact(-52_000m, "USD"), StringComparison.Ordinal);
    }
}
