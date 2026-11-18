using System.Globalization;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance.Instruments;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// <see cref="InstrumentVisuals"/> (issue #452): the close formatter, the one change announcement per row
/// (§6.2), the stale chip and the Oslo-date degradation (§8) the pages and the Watchlist card share.
/// </summary>
public class InstrumentVisualsTests
{
    private static T InCulture<T>(string name, Func<T> act)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(name);
            return act();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static string Invariant<T>(Func<T> act) where T : notnull => InCulture("en-US", act).ToString()!;

    [Theory]
    [InlineData("228.1", 2, "228.10 USD")]
    [InlineData("284.60000000", 2, "284.60 NOK")]
    [InlineData("0.12345678", 2, "0.12345678 USD")]
    [InlineData("274.500000000", 2, "274.50 NOK")]
    [InlineData("1234.5", 2, "1,234.50 NOK")]
    [InlineData("2841", 0, "2,841 JPY")]
    [InlineData("2841.25", 0, "2,841.25 JPY")]
    [InlineData("10.123", 3, "10.123 KWD")]
    public void A_close_shows_the_currencys_decimals_up_to_eight_with_trailing_zeros_trimmed(string close, int minor, string expected)
    {
        var code = expected[^3..];
        Assert.Equal(expected, Invariant(() =>
            InstrumentVisuals.FormatClose(decimal.Parse(close, CultureInfo.InvariantCulture), code, minor)));
    }

    [Fact]
    public void A_close_with_exactly_eight_decimals_round_trips()
    {
        Assert.Equal("1.00000001 NOK", Invariant(() => InstrumentVisuals.FormatClose(1.00000001m, "nok")));
    }

    [Fact]
    public void A_missing_close_is_a_dash_and_a_missing_code_is_left_bare()
    {
        Assert.Equal("—", InstrumentVisuals.FormatClose(null, "NOK"));
        Assert.Equal("12.50", Invariant(() => InstrumentVisuals.FormatClose(12.5m, null)));
    }

    [Fact]
    public void A_close_groups_digits_by_the_current_culture()
    {
        var text = InCulture("nb-NO", () => InstrumentVisuals.FormatClose(1234.5m, "NOK"));
        Assert.EndsWith(",50 NOK", text);
        Assert.StartsWith("1", text);
    }

    [Theory]
    [InlineData("1.2", "29.72", "Up 1.20 percent, 29.72 NOK")]
    [InlineData("-0.854", "-2.41", "Down 0.85 percent, 2.41 NOK")]
    [InlineData("0.004", "0.01", "No change")]
    [InlineData("0", "0", "No change")]
    [InlineData("-0.001", null, "No change")]
    [InlineData("3.5", null, "Up 3.50 percent")]
    public void The_change_is_announced_once_in_words(string percent, string? change, string expected)
    {
        Assert.Equal(expected, Invariant(() => InstrumentVisuals.ChangeAnnouncement(
            decimal.Parse(percent, CultureInfo.InvariantCulture),
            change is null ? null : decimal.Parse(change, CultureInfo.InvariantCulture),
            "NOK",
            2)));
    }

    [Fact]
    public void A_row_without_a_previous_price_says_so()
    {
        Assert.Equal("No previous price", InstrumentVisuals.ChangeAnnouncement(null, null, "NOK"));
        Assert.Equal("No previous price", InstrumentVisuals.ChangeAnnouncement(
            new InstrumentWatchlistItem { InstrumentId = Guid.NewGuid(), Symbol = "EQNR", Name = "Equinor", NativeCurrencyCode = "NOK" },
            "NOK", 2));
    }

    [Fact]
    public void The_announcement_takes_the_display_currencys_decimals()
    {
        Assert.Equal("Down 2.00 percent, 58 JPY",
            Invariant(() => InstrumentVisuals.ChangeAnnouncement(-2m, -58m, "JPY", 0)));
    }

    [Theory]
    [InlineData("1.204", "1.20 %")]
    [InlineData("-0.856", "0.86 %")]
    [InlineData("0.001", "0.00 %")]
    public void The_visual_percentage_is_unsigned_with_two_decimals(string percent, string expected)
    {
        Assert.Equal(expected, Invariant(() =>
            InstrumentVisuals.FormatChangePercent(decimal.Parse(percent, CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void The_stale_chip_counts_days_and_degrades_to_a_bare_label()
    {
        var today = new DateOnly(2026, 11, 9);
        Assert.Equal("Stale · 10 days", InstrumentVisuals.StaleText(new DateOnly(2026, 10, 30), today));
        Assert.Equal("Stale · 1 day", InstrumentVisuals.StaleText(new DateOnly(2026, 11, 8), today));
        Assert.Equal("Stale", InstrumentVisuals.StaleText(new DateOnly(2026, 10, 30), null));
        Assert.Equal("Stale", InstrumentVisuals.StaleText(null, today));
    }

    [Fact]
    public void Europe_Oslo_resolves_in_the_client_test_host()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 8, 23, 30, 0, TimeSpan.Zero));

        // 23:30 UTC on 8 November is already 9 November in Oslo (UTC+1) — a UTC fallback would be a day off.
        Assert.Equal(new DateOnly(2026, 11, 9), InstrumentVisuals.TryOsloToday(clock));
    }

    [Fact]
    public void Price_vocabulary_reads_as_words()
    {
        Assert.Equal("Current", InstrumentVisuals.StatusOf(InstrumentPriceStatus.Current).Label);
        Assert.Equal("Historical", InstrumentVisuals.StatusOf(InstrumentPriceStatus.Historical).Label);
        Assert.Equal("Manual", InstrumentVisuals.SourceCellText(InstrumentPriceSource.Manual, null));
        Assert.Equal("Fetched · EODHD · read-only", InstrumentVisuals.SourceCellText(InstrumentPriceSource.Fetched, "EODHD"));
        Assert.Equal("Fetched · Unknown source · read-only", InstrumentVisuals.SourceCellText(InstrumentPriceSource.Fetched, " "));
        Assert.Equal("Unknown", InstrumentVisuals.SourceOf((InstrumentPriceSource)42).Label);
    }

    [Fact]
    public void Types_read_from_the_registry_and_an_unknown_ordinal_reads_as_other()
    {
        Assert.Equal(("ETF", "stacked_line_chart"), (InstrumentVisuals.TypeOf(InstrumentType.Etf).Label, InstrumentVisuals.TypeOf(InstrumentType.Etf).Icon));
        Assert.Equal(("Stock", "show_chart"), (InstrumentVisuals.TypeOf(InstrumentType.Stock).Label, InstrumentVisuals.TypeOf(InstrumentType.Stock).Icon));
        Assert.Equal(("Fund", "savings"), (InstrumentVisuals.TypeOf(InstrumentType.Fund).Label, InstrumentVisuals.TypeOf(InstrumentType.Fund).Icon));
        Assert.Equal("Other", InstrumentVisuals.TypeOf((InstrumentType)5).Label);
        Assert.Same(OdsTypeRegistries.InstrumentTypes[^1], OdsTypeRegistries.InstrumentTypeOf((InstrumentType)5));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
