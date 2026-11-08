using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

/// <summary>
/// The words weather speaks on a journal entry, an agenda row and the event dialog (issue #363 §3.2–§3.4; design
/// system · WeatherCalendarJournal.jsx). Every number goes through <see cref="WeatherVisuals"/>, so the true minus
/// and the units have one source; this class only composes sentences.
/// </summary>
internal static class WeatherEventText
{
    /// <summary>Snow and sleet are precipitation, not rain.</summary>
    public static string PrecipitationNoun(string? symbolCode) =>
        WeatherVisuals.SymbolOf(symbolCode)?.Label is { } label
        && (label.Contains("snow", StringComparison.OrdinalIgnoreCase) || label.Contains("sleet", StringComparison.OrdinalIgnoreCase))
            ? "precipitation"
            : "rain";

    /// <summary><i>7.4 mm of rain</i>, <i>no rain</i>, or null when the amount is unknown.</summary>
    public static string? PrecipitationWords(decimal? mm, string? symbolCode) => mm switch
    {
        null => null,
        > 0 => $"{WeatherVisuals.Mm(mm)} of {PrecipitationNoun(symbolCode)}",
        _ => $"no {PrecipitationNoun(symbolCode)}",
    };

    /// <summary>
    /// The journal disclosure's text, which is also its whole accessible name: <i>Weather: Rain, high 8°, low 4°,
    /// 7.4 mm of rain, measured</i>. The measured half wins when Frost has the day; the condition words always come
    /// from the forecast's symbol, since measured days carry none.
    /// </summary>
    public static string JournalLine(WeatherDaySummary day)
    {
        var forecast = day.Forecast;
        var (min, max, mm) = day.Observed is { } o
            ? (o.MinTempC, o.MaxTempC, o.PrecipitationMm)
            : (forecast?.MinTempC, forecast?.MaxTempC, forecast?.PrecipitationMm);
        var parts = new List<string>();
        if (WeatherVisuals.SymbolOf(forecast?.SymbolCode) is { } symbol)
            parts.Add(symbol.Label);
        if (max is not null)
            parts.Add($"high {WeatherVisuals.Temp(max)}");
        if (min is not null)
            parts.Add($"low {WeatherVisuals.Temp(min)}");
        if (PrecipitationWords(mm, forecast?.SymbolCode) is { } rain)
            parts.Add(rain);
        parts.Add(day.Observed is not null ? "measured" : "forecast");
        return $"Weather: {string.Join(", ", parts)}";
    }

    /// <summary>Where the weather comes from, in one sentence. <paramref name="noun"/> is <i>entry</i> or <i>event</i>.</summary>
    public static string? Where(WeatherResolvedFrom? from, WeatherPointRef? point, WeatherLinkedLocationRef? linked, int? distanceKm, string noun)
    {
        if (point is null)
            return null;

        return from switch
        {
            WeatherResolvedFrom.Linked => distanceKm is { } km
                ? $"Weather at {point.Name}, {WeatherVisuals.Km(km)} from {linked?.Name ?? "its place"}."
                : $"Weather at {point.Name}. The distance from {linked?.Name ?? "its place"} is unknown.",
            WeatherResolvedFrom.Preferred => $"Weather at {point.Name}, your default weather point. This {noun} has no place.",
            WeatherResolvedFrom.Household => $"Weather at {point.Name}, the household default. This {noun} has no place.",
            _ => $"Weather at {point.Name}.",
        };
    }

    /// <summary>The agenda glyph's name: <i>Weather: partly cloudy</i>, so the summary is not heard as part of the title.</summary>
    public static string? GlyphName(string? symbolCode) =>
        WeatherVisuals.SymbolOf(symbolCode) is { } symbol ? $"Weather: {symbol.Label.ToLowerInvariant()}" : null;

    /// <summary><i>6° to 9°</i>; one figure when both round to the same; never an en-dash, so a negative bound reads.</summary>
    public static string? TemperatureSpan(decimal? min, decimal? max)
    {
        var low = min is null ? null : WeatherVisuals.Temp(min);
        var high = max is null ? null : WeatherVisuals.Temp(max);
        return (low, high) switch
        {
            ({ } l, { } h) when l == h => h,
            ({ } l, { } h) => $"{l} to {h}",
            _ => high ?? low,
        };
    }

    /// <summary>
    /// <i>0.4 mm</i>, or <i>0.8 mm in 6 h</i> when the amount covers more hours than the event lasts — a short event
    /// inside a 6-hour block does not own the block's whole amount. Null when dry or unknown.
    /// </summary>
    public static string? AgendaPrecipitation(Odyssey.Dtos.Journal.WeatherEventSummary summary, double eventHours) =>
        summary.PrecipitationMm is > 0
            ? summary.PrecipitationHours is { } hours && hours > eventHours
                ? $"{WeatherVisuals.Mm(summary.PrecipitationMm)} in {hours} h"
                : WeatherVisuals.Mm(summary.PrecipitationMm)
            : null;

    /// <summary>"Past" is the event's end instant before now, both UTC: no clock or zone is involved.</summary>
    public static bool IsPast(DateTime end, DateTime utcNow) => Utc(end) < utcNow;

    internal static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
