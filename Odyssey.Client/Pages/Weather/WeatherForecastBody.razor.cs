using Microsoft.AspNetCore.Components;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

public partial class WeatherForecastBody
{
    /// <summary>An Ok current read with its step and point.</summary>
    [Parameter, EditorRequired] public WeatherCurrentResponse Current { get; set; } = default!;

    /// <summary>The daily read for the same point; days it lacks are simply not shown.</summary>
    [Parameter] public IReadOnlyList<WeatherDaySummary> Days { get; set; } = [];

    /// <summary>Up to nine days from today, as the design shows.</summary>
    internal const int DaysShown = 9;

    /// <summary>The POINT's today when the response carries it — its local date, which the server computes — else the viewer's.</summary>
    private DateOnly Today => Current.Today?.Date ?? DateOnly.FromDateTime(WeatherWidget.LocalNow());

    private WeatherDaySummary? TodaySummary =>
        Current.Today ?? Days.FirstOrDefault(d => d.Date == Today && d.Forecast is not null);

    private IReadOnlyList<WeatherDaySummary> Coming =>
        Days.Where(d => d.Forecast is not null && d.Date >= Today).Take(DaysShown).ToList();

    private WeatherDaySummary? Yesterday =>
        Days.FirstOrDefault(d => d.Date == Today.AddDays(-1) && d.Observed is not null);

    internal static string DaySentence(WeatherDaySummary day) =>
        $"{WeatherVisuals.SymbolOf(day.Forecast?.SymbolCode)?.Label ?? "Unknown conditions"}, high {WeatherVisuals.Temp(day.Forecast?.MaxTempC)}, low {WeatherVisuals.Temp(day.Forecast?.MinTempC)}";
}
