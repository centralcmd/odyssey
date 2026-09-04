using Odyssey.Client.Components;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The money-text contract: what a keystroke may do to an amount, and what that amount becomes on
/// submit. Both halves live in one place precisely because a disagreement between them is silent —
/// <see cref="OdsMoneyText.Sanitize"/> accepts a lone comma as a decimal separator, so a parser that
/// treated it as a thousands separator turned "1234,56" into 123456 with no error shown. These tests
/// pin the pair together.
/// </summary>
public class OdsMoneyTextTests
{
    // ── Sanitize ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("12a34", "1234")]           // letters are never part of an amount
    [InlineData("1 234.50", "1 234.50")]    // a space is a group separator, kept
    [InlineData("12$", "12")]               // no symbol: the currency lives in its own segment
    [InlineData("", "")]
    public void A_stray_character_is_dropped(string typed, string expected) =>
        Assert.Equal(expected, OdsMoneyText.Sanitize(typed, allowNegative: true));

    [Theory]
    [InlineData("1.2.3")]  // a second decimal separator
    [InlineData("1,2.3")]  // …in either notation
    [InlineData("1,2,3")]
    public void A_second_decimal_separator_is_rejected(string typed) =>
        // null, not a repaired string: the caller puts the field back to what it held, so a stray key
        // never silently relocates the separator later.
        Assert.Null(OdsMoneyText.Sanitize(typed, allowNegative: true));

    [Theory]
    [InlineData("-5", "-5")]
    [InlineData("-", "-")]
    public void A_leading_minus_is_accepted_when_negatives_are_meaningful(string typed, string expected) =>
        Assert.Equal(expected, OdsMoneyText.Sanitize(typed, allowNegative: true));

    [Theory]
    [InlineData("--5")]
    [InlineData("---")]
    [InlineData("--")]
    public void A_SECOND_leading_minus_is_rejected(string typed) =>
        // Regression: testing only the FIRST index ("IndexOf('-') > 0") let a second minus through at
        // index 0, so an ordinary double keypress left the field stuck showing "--5".
        Assert.Null(OdsMoneyText.Sanitize(typed, allowNegative: true));

    [Fact]
    public void A_non_leading_minus_is_rejected() =>
        Assert.Null(OdsMoneyText.Sanitize("4-2", allowNegative: true));

    [Fact]
    public void A_minus_is_dropped_outright_where_a_negative_is_meaningless() =>
        // Not rejected — the character simply isn't in the accepted set, so the rest of the keystroke
        // still lands.
        Assert.Equal("42", OdsMoneyText.Sanitize("-42", allowNegative: false));

    // ── Parse ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_COMMA_is_a_decimal_point_not_a_thousands_separator() =>
        // Regression: the five dialogs that stripped commas as group separators read this as 123456 —
        // a hundredfold error on submit, with no validation firing and nothing shown to the user.
        Assert.Equal(1234.56m, OdsMoneyText.Parse("1234,56"));

    [Theory]
    [InlineData("1234.56", 1234.56)]
    [InlineData("1 234,50", 1234.50)]     // space groups, comma decimates
    [InlineData("-42", -42)]
    [InlineData("0", 0)]
    public void An_amount_parses_invariantly(string typed, double expected) =>
        Assert.Equal((decimal)expected, OdsMoneyText.Parse(typed));

    [Theory]
    [InlineData("1 250,", 1250)]
    [InlineData("12.", 12)]
    public void A_trailing_separator_is_half_typed_not_malformed(string typed, double expected) =>
        // Falling to null here would silently clear the figure the user is still typing into.
        Assert.Equal((decimal)expected, OdsMoneyText.Parse(typed));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData(".")]
    [InlineData("not a number")]
    public void There_is_no_value_to_read(string? typed) =>
        Assert.Null(OdsMoneyText.Parse(typed));

    // ── The pair ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1234,56", 1234.56)]
    [InlineData("1 234,56", 1234.56)]
    [InlineData("1234.56", 1234.56)]
    [InlineData("-99,5", -99.5)]
    public void Anything_Sanitize_accepts_Parse_reads_back_at_the_same_value(string typed, double expected)
    {
        // The contract the two halves exist to keep: a keystroke sequence the field ALLOWS must not
        // then mean something different on submit.
        var sanitized = OdsMoneyText.Sanitize(typed, allowNegative: true);

        Assert.NotNull(sanitized);
        Assert.Equal((decimal)expected, OdsMoneyText.Parse(sanitized));
    }
}
