using System.Globalization;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance.Instruments;

/// <summary>A price row's status or source as words and a glyph (design system · <c>instruments-data.js</c> <c>INS_VOCAB</c>).</summary>
public sealed record InstrumentVocabInfo(string Label, string Icon);

/// <summary>
/// Display rules shared by the Instruments and Instrument prices pages and the dashboard Watchlist card
/// (issue #452; design system · <c>Instruments.jsx</c>, <c>InstrumentPrices.jsx</c>, <c>Dashboard.jsx</c>). The type
/// registry itself is <see cref="OdsTypeRegistries.InstrumentTypes"/>; this file holds the words a figure becomes.
/// Nothing here restates a server value: decimals are <see cref="InstrumentPriceRules.MaxDecimals"/>, the stale
/// threshold arrives on the watchlist response, and today's date is <see cref="OsloDate"/>.
/// </summary>
public static class InstrumentVisuals
{
    /// <summary>The figure a missing close or change renders as — an em dash, never "0".</summary>
    public const string Absent = OdsMoney.Absent;

    private static readonly InstrumentVocabInfo Unknown = new("Unknown", "help");

    /// <summary>The type's label, glyph and colour; an unknown ordinal reads as "Other".</summary>
    public static OdsTypeOption TypeOf(InstrumentType type) => OdsTypeRegistries.InstrumentTypeOf(type);

    /// <summary>"Current" (the newest-rule winner) or "Historical".</summary>
    public static InstrumentVocabInfo StatusOf(InstrumentPriceStatus status) => status switch
    {
        InstrumentPriceStatus.Current => new("Current", "bolt"),
        InstrumentPriceStatus.Historical => new("Historical", "history"),
        _ => Unknown,
    };

    /// <summary>"Manual" or "Fetched".</summary>
    public static InstrumentVocabInfo SourceOf(InstrumentPriceSource source) => source switch
    {
        InstrumentPriceSource.Manual => new("Manual", "edit_note"),
        InstrumentPriceSource.Fetched => new("Fetched", "cloud_download"),
        _ => Unknown,
    };

    /// <summary>
    /// A price row's Source cell (issue #452 §3.3): "Manual", or "Fetched · {source} · read-only" — a fetched row
    /// carries no menu, so the cell says why. The source name is plain text from the server.
    /// </summary>
    public static string SourceCellText(InstrumentPriceSource source, string? sourceDisplayName) =>
        source == InstrumentPriceSource.Fetched
            ? $"Fetched · {(string.IsNullOrWhiteSpace(sourceDisplayName) ? "Unknown source" : sourceDisplayName.Trim())} · read-only"
            : SourceOf(source).Label;

    /// <summary>
    /// A native close as the amount then its ISO code (<c>228.10 USD</c>, <c>0.12345678 USD</c>): at least the
    /// currency's own decimals, up to <see cref="InstrumentPriceRules.MaxDecimals"/>, with trailing zeros beyond the
    /// currency's decimals trimmed. Digit grouping follows the current culture, as <see cref="OdsMoney"/> does.
    /// </summary>
    /// <param name="minorUnits">The currency's decimals (<see cref="OdsMoney.MinorUnitsOf"/>); 2 when unknown.</param>
    public static string FormatClose(decimal? close, string? currencyCode, int minorUnits = OdsMoney.DefaultMinorUnits)
    {
        if (close is not { } value)
        {
            return Absent;
        }

        var max = InstrumentPriceRules.MaxDecimals;
        var min = Math.Clamp(minorUnits, 0, max);
        var pattern = "#,##0" + (max > 0 ? "." + new string('0', min) + new string('#', max - min) : string.Empty);
        var magnitude = Math.Round(Math.Abs(value), max, MidpointRounding.AwayFromZero)
            .ToString(pattern, CultureInfo.CurrentCulture);
        var code = string.IsNullOrWhiteSpace(currencyCode) ? string.Empty : $" {currencyCode.Trim().ToUpperInvariant()}";
        return $"{OdsMoney.Slot(value, signed: false)}{magnitude}{code}";
    }

    /// <summary>Whether a day-over-day change reads as no change: its percentage rounds to 0.00.</summary>
    public static bool IsZeroChange(decimal changePercent) =>
        Math.Round(changePercent, 2, MidpointRounding.AwayFromZero) == 0m;

    /// <summary>The visual percentage, unsigned (the direction is the delta's arrow): <c>1.20 %</c>, <c>0.00 %</c>.</summary>
    public static string FormatChangePercent(decimal changePercent) =>
        $"{Math.Abs(Math.Round(changePercent, 2, MidpointRounding.AwayFromZero)).ToString("N2", CultureInfo.CurrentCulture)} %";

    /// <summary>
    /// The one screen-reader string for a row's change (issue #452 §6.2): "Up 1.20 percent, 29.72 NOK",
    /// "Down 0.85 percent, 2.41 NOK", "No change" or "No previous price". The visual delta beside it is
    /// <c>aria-hidden</c>, so this is the only place the direction is said. The amount is left out when the server
    /// sent none (an unconvertible row).
    /// </summary>
    /// <param name="minorUnits">The display currency's decimals.</param>
    public static string ChangeAnnouncement(
        decimal? changePercent, decimal? change, string? displayCurrencyCode, int minorUnits = OdsMoney.DefaultMinorUnits)
    {
        if (changePercent is not { } percent)
        {
            return "No previous price";
        }

        if (IsZeroChange(percent))
        {
            return "No change";
        }

        var direction = percent > 0 ? "Up" : "Down";
        var words = $"{direction} {Math.Abs(Math.Round(percent, 2, MidpointRounding.AwayFromZero)).ToString("N2", CultureInfo.CurrentCulture)} percent";
        return change is { } amount
            ? $"{words}, {OdsMoney.Format(Math.Abs(amount), displayCurrencyCode, minorUnits)}"
            : words;
    }

    /// <summary>The announcement for a watchlist row, in the watchlist's display currency.</summary>
    public static string ChangeAnnouncement(InstrumentWatchlistItem item, string displayCurrencyCode, int minorUnits) =>
        ChangeAnnouncement(item.ChangePercent, item.Change, displayCurrencyCode, minorUnits);

    /// <summary>
    /// Today in Europe/Oslo, or <see langword="null"/> when the zone cannot be resolved (#446 §8.7). The caller then
    /// skips the future-date check (the server's <c>422</c> remains) and shows "Stale" without an age.
    /// </summary>
    public static DateOnly? TryOsloToday(TimeProvider timeProvider)
    {
        try
        {
            return OsloDate.Today(timeProvider);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>
    /// The stale chip (issue #452 §3.2): "Stale · 4 days" counted from the latest trading date to Oslo's today, or
    /// plain "Stale" when either date is unknown. Whether a row IS stale is the server's <c>isStale</c>.
    /// </summary>
    public static string StaleText(DateOnly? latestTradingDate, DateOnly? today)
    {
        if (latestTradingDate is not { } latest || today is not { } now)
        {
            return "Stale";
        }

        var days = now.DayNumber - latest.DayNumber;
        return days <= 0 ? "Stale" : $"Stale · {days} {(days == 1 ? "day" : "days")}";
    }
}
