using System.Globalization;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

/// <summary>How a measure is drawn: shape tells two measures on one axis apart, not colour alone.</summary>
internal enum WeatherMeasureMark { Band, Bar, Line, Dots }

/// <summary>
/// One measure the Weather history can plot (design system · Weather.jsx <c>WX_MEASURES</c>). <see cref="Group"/>
/// is the unit: as on the contract-terms chart, a measure in another group REPLACES the selection rather than
/// sharing an axis true of neither. <see cref="Color"/> is the measure's own hue and never moves.
/// </summary>
internal sealed record WeatherMeasure(
    string Key,
    string Label,
    string Group,
    WeatherMeasureGroup StationGroup,
    string Color,
    WeatherMeasureMark Mark,
    string Unit,
    Func<WeatherDayObserved, decimal?> Pick,
    Func<decimal, string> Format);

/// <summary>The one registry the picker, the marks, the ledger, the facts and the hidden table share.</summary>
internal static class WeatherMeasures
{
    public const string Temperature = "temp";
    public const string Precipitation = "precip";
    public const string Wind = "wind";
    public const string Gust = "gust";

    private static string One(decimal v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    public static readonly IReadOnlyList<WeatherMeasure> All =
    [
        new(Temperature, "Temperature", "c", WeatherMeasureGroup.Temperature, "var(--brand-text)", WeatherMeasureMark.Band, "°", o => o.MeanTempC, v => WeatherVisuals.Temp(v)),
        new(Precipitation, "Precipitation", "mm", WeatherMeasureGroup.Precipitation, "var(--chart-2)", WeatherMeasureMark.Bar, "mm", o => o.PrecipitationMm, v => $"{One(v)} mm"),
        new(Wind, "Mean wind", "ms", WeatherMeasureGroup.Wind, "var(--chart-4)", WeatherMeasureMark.Line, "m/s", o => o.MeanWindSpeedMps, v => $"{One(v)} m/s"),
        new(Gust, "Gusts", "ms", WeatherMeasureGroup.Wind, "var(--chart-6)", WeatherMeasureMark.Dots, "m/s", o => o.MaxWindGustMps, v => $"{One(v)} m/s"),
    ];

    public static WeatherMeasure Of(string key) => All.First(m => m.Key == key);

    /// <summary>
    /// The measures a place can show: those whose group has a station. Before any lookup has answered (no
    /// stations listed) every measure is offered — nothing is known to be missing.
    /// </summary>
    public static IReadOnlyList<WeatherMeasure> Offered(IReadOnlyList<WeatherStationRef> stations) =>
        stations.Count == 0
            ? All
            : [.. All.Where(m => stations.Any(st => st.Group == m.StationGroup && st.Name is not null))];

    /// <summary>The section's own name for a group, as the info button lists it.</summary>
    public static string GroupLabel(WeatherMeasureGroup group) => group switch
    {
        WeatherMeasureGroup.Precipitation => "Precipitation",
        WeatherMeasureGroup.Wind => "Wind",
        _ => "Temperature",
    };

    /// <summary>"Temperature: Oslo - Blindern, 3 km", or the reach searched for a group with none.</summary>
    public static string StationLine(WeatherStationRef station) => station.Name is { } name
        ? $"{GroupLabel(station.Group)}: {name}, {station.DistanceKm} km"
        : $"{GroupLabel(station.Group)}: no station within {station.MaxDistanceKm} km";

    /// <summary>The value a measure states for a whole window, in the ledger.</summary>
    public static string WindowFigure(WeatherMeasure m, IReadOnlyList<decimal> values)
    {
        if (values.Count == 0)
            return "—";
        var sum = values.Sum();
        return m.Key switch
        {
            Precipitation => $"{One(sum)} mm total",
            Gust => $"{One(values.Max())} m/s max",
            Wind => $"{One(sum / values.Count)} m/s avg",
            _ => $"{WeatherVisuals.Temp(sum / values.Count)} avg",
        };
    }

    /// <summary>
    /// The contract-terms chart's selection rule: never nothing plotted, and a measure in another unit replaces the
    /// selection instead of joining an axis true of neither.
    /// </summary>
    public static IReadOnlyList<string> NextSelection(IReadOnlyList<string> current, IReadOnlyCollection<string> next)
    {
        var added = next.Where(k => !current.Contains(k)).ToList();
        if (added.Count == 0)
            return next.Count > 0 ? [.. current.Where(next.Contains)] : [current[0]];
        var key = added[^1];
        return Of(key).Group == Of(current[0]).Group ? [.. current, key] : [key];
    }
}
