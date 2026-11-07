using Odyssey.Context;
using Odyssey.Dtos.Journal;

namespace Odyssey.Core.Weather;

/// <summary>
/// The event-weather rules the single calendar read and the calendar batch share (issue #430 §3.2.7, §5.3): one
/// window, one row selection and one summary composition, so an agenda row and the event dialog can never
/// disagree about whether hourly weather exists.
/// </summary>
public static class WeatherEventWeather
{
    /// <summary>A timed event's weather never covers more than this; longer events show their first day.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromHours(24);

    /// <summary>A zero-length (or inverted) event still gets the hour that covers its start.</summary>
    public static readonly TimeSpan MinWindow = TimeSpan.FromHours(1);

    /// <summary>The weather window of a timed event: <c>[start, min(end, start + 24 h))</c>, at least one hour.</summary>
    public static (DateTime FromUtc, DateTime ToUtc) Window(DateTime startUtc, DateTime endUtc)
    {
        var to = endUtc - startUtc > MaxWindow ? startUtc + MaxWindow : endUtc;
        return (startUtc, to > startUtc ? to : startUtc + MinWindow);
    }

    /// <summary>
    /// Where the effective-steps read must start so a step that straddles <paramref name="fromUtc"/> is included:
    /// the longest stored period before it.
    /// </summary>
    public static DateTime ReadFrom(DateTime fromUtc) => fromUtc.AddHours(-WeatherComposer.LongestPeriodHours);

    /// <summary>
    /// The rows of a window: the effective steps that overlap it, finer steps winning over a coarser block they
    /// overlap, never overlapping each other (<see cref="WeatherComposer.DayRows"/>).
    /// </summary>
    public static List<WeatherForecastStep> Rows(IReadOnlyList<WeatherForecastStep> effective, DateTime fromUtc, DateTime toUtc) =>
        WeatherComposer.DayRows(effective, fromUtc, toUtc);

    /// <summary>
    /// A timed event's summary from its rows. The symbol is the step covering the start (else the first row);
    /// temperatures fold air temperature with the steps' own min/max, as <see cref="WeatherComposer.ForecastHalf"/>
    /// does; rain is the rows' sum, each row whole, and <c>PrecipitationHours</c> says how many hours it spans.
    /// Null when there are no rows.
    /// </summary>
    public static WeatherEventSummary? Timed(IReadOnlyList<WeatherForecastStep> effective, DateTime startUtc, DateTime fromUtc, DateTime toUtc)
    {
        var rows = Rows(effective, fromUtc, toUtc);
        if (rows.Count == 0)
        {
            return null;
        }

        var cover = WeatherComposer.Covering(rows, startUtc) ?? rows[0];
        var temps = rows.SelectMany(r => new[] { r.AirTemperatureC, r.TempMinC, r.TempMaxC })
            .Where(v => v is not null).Select(v => v!.Value).ToList();
        var wet = rows.Where(r => r.PrecipitationMm is not null).ToList();
        return new WeatherEventSummary
        {
            Basis = WeatherSummaryBasis.Hourly,
            SymbolCode = cover.SymbolCode,
            MinTempC = temps.Count > 0 ? temps.Min() : null,
            MaxTempC = temps.Count > 0 ? temps.Max() : null,
            PrecipitationMm = wet.Count > 0 ? wet.Sum(r => r.PrecipitationMm!.Value) : null,
            PrecipitationHours = wet.Count > 0 ? wet.Sum(r => (int)r.PeriodHours) : null,
            IssuedAtUtc = DateTime.SpecifyKind(rows.Max(r => r.RunProviderUpdatedAt), DateTimeKind.Utc),
            Measured = false,
        };
    }

    /// <summary>An all-day event's summary from its first day: the measured half when Frost has it, else the forecast half.</summary>
    public static WeatherEventSummary? Daily(WeatherDaySummary? first)
    {
        if (first is null)
        {
            return null;
        }

        if (first.Observed is { } o)
        {
            return new WeatherEventSummary
            {
                Basis = WeatherSummaryBasis.Daily,
                SymbolCode = first.Forecast?.SymbolCode,
                MinTempC = o.MinTempC,
                MaxTempC = o.MaxTempC,
                PrecipitationMm = o.PrecipitationMm,
                IssuedAtUtc = first.Forecast?.IssuedAtUtc,
                Measured = true,
            };
        }

        return first.Forecast is { } f
            ? new WeatherEventSummary
            {
                Basis = WeatherSummaryBasis.Daily,
                SymbolCode = f.SymbolCode,
                MinTempC = f.MinTempC,
                MaxTempC = f.MaxTempC,
                PrecipitationMm = f.PrecipitationMm,
                IssuedAtUtc = f.IssuedAtUtc,
                Measured = false,
            }
            : null;
    }
}
