using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Odyssey.Client.Components;
using Odyssey.Dtos.Journal;

namespace Odyssey.Client.Pages.Weather;

/// <summary>
/// The measured daily temperature of one place over a window of days (design system · Weather.jsx
/// <c>WeatherTemperatureChart</c>): a band from each day's low to high and a line through the daily mean.
/// Only the observed half of a day is plotted — a forecast for a day that has passed is a prediction, not
/// history. A day the station did not report is a gap: band and line both break, and a lone reported day
/// between two gaps draws as a bar with a dot. Nothing is joined across a missing day.
/// </summary>
public partial class WeatherTemperatureChart : IAsyncDisposable
{
    [Parameter, EditorRequired] public IReadOnlyList<WeatherDaySummary> Days { get; set; } = [];
    [Parameter, EditorRequired] public DateOnly From { get; set; }
    [Parameter, EditorRequired] public DateOnly To { get; set; }
    [Parameter, EditorRequired] public string LocationName { get; set; } = "";

    [Inject] private IJSRuntime JS { get; set; } = default!;

    internal sealed record Column(int Index, DateOnly Date, WeatherDayObserved? Observed)
    {
        public bool HasBand => Observed is { MinTempC: not null, MaxTempC: not null };
        public bool HasMean => Observed is { MeanTempC: not null };
    }

    // As LineChart: the viewBox tracks the plot's pixel width, so axis text stays at --fs-micro at any width.
    internal const double ViewBoxHeight = 236;
    private const double YTop = 14, YBot = 200, X0 = 46, RightInset = 10;
    private double _vbW = 720;
    private double X1 => _vbW - RightInset;

    private ElementReference _plotRef;
    private PlotWidthObserver? _widthObserver;

    private Column[] _cols = [];
    private double[] _ticks = [];
    private double _yMin, _yMax;
    private int? _hover;
    private bool _hoverByKey;

    private int N => _cols.Length;
    private double ColW => (X1 - X0) / N;
    private double Cx(int i) => X0 + (i + 0.5) * ColW;
    private double Sy(double v) => YBot - (v - _yMin) / (_yMax - _yMin != 0 ? _yMax - _yMin : 1) * (YBot - YTop);
    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    private static double D(decimal? v) => (double)v!.Value;

    protected override void OnParametersSet()
    {
        var byDate = Days.Where(d => d.Observed is not null).ToDictionary(d => d.Date, d => d.Observed);
        var n = To.DayNumber - From.DayNumber + 1;
        _cols = [.. Enumerable.Range(0, Math.Max(n, 0)).Select(i =>
        {
            var date = From.AddDays(i);
            return new Column(i, date, byDate.GetValueOrDefault(date));
        })];
        if (_hover >= N) _hover = null;

        var values = _cols.Where(c => c.Observed is not null)
            .SelectMany(c => new[] { c.Observed!.MinTempC, c.Observed.MaxTempC, c.Observed.MeanTempC })
            .Where(v => v is not null).Select(v => (double)v!.Value).ToList();
        var lo = values.Count > 0 ? values.Min() : 0;
        var hi = values.Count > 0 ? values.Max() : 0;
        var step = hi - lo <= 8 ? 2 : hi - lo <= 24 ? 5 : 10;
        _yMin = Math.Floor((lo - 1) / step) * step;
        _yMax = Math.Ceiling((hi + 1) / step) * step;
        var ticks = new List<double>();
        for (var v = _yMin; v <= _yMax; v += step) ticks.Add(v);
        _ticks = [.. ticks];
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _widthObserver ??= new PlotWidthObserver(JS, OnPlotWidth);
        await _widthObserver.SyncAsync(N > 0 ? _plotRef : null);
    }

    private Task OnPlotWidth(double width) => InvokeAsync(() =>
    {
        var w = Math.Round(width);
        if (w <= 0 || w == _vbW) return;
        _vbW = w;
        StateHasChanged();
    });

    public async ValueTask DisposeAsync()
    {
        if (_widthObserver is not null)
            await _widthObserver.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // ── Runs and facts ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Maximal runs of consecutive columns for which <paramref name="has"/> holds.</summary>
    internal static List<List<Column>> Runs(IEnumerable<Column> cols, Func<Column, bool> has)
    {
        var runs = new List<List<Column>>();
        List<Column>? current = null;
        foreach (var c in cols)
        {
            if (has(c)) { if (current is null) { current = []; runs.Add(current); } current.Add(c); }
            else current = null;
        }
        return runs;
    }

    private List<List<Column>> BandRuns => Runs(_cols, c => c.HasBand);
    private List<List<Column>> MeanRuns => Runs(_cols, c => c.HasMean);
    private int MeasuredCount => _cols.Count(c => c.Observed is not null);
    private bool Gaps => MeasuredCount < N;

    private string BandPath(List<Column> run) =>
        "M " + string.Join(" L ", run.Select(c => $"{F(Cx(c.Index))} {F(Sy(D(c.Observed!.MaxTempC)))}"))
        + " L " + string.Join(" L ", Enumerable.Reverse(run).Select(c => $"{F(Cx(c.Index))} {F(Sy(D(c.Observed!.MinTempC)))}"))
        + " Z";

    private string MeanPoints(List<Column> run) =>
        string.Join(' ', run.Select(c => $"{F(Cx(c.Index))},{F(Sy(D(c.Observed!.MeanTempC)))}"));

    private string Range => $"{WeatherVisuals.Date(From)} to {WeatherVisuals.Date(To)}";

    private string AriaLabel
    {
        get
        {
            var band = _cols.Where(c => c.HasBand).ToList();
            var lowest = band.MinBy(c => c.Observed!.MinTempC);
            var highest = band.MaxBy(c => c.Observed!.MaxTempC);
            return $"Measured daily temperature at {LocationName}, {Range}."
                + (lowest is not null && highest is not null
                    ? $" Lowest {WeatherVisuals.Temp(lowest.Observed!.MinTempC)} on {WeatherVisuals.DayLong(lowest.Date)}, highest {WeatherVisuals.Temp(highest.Observed!.MaxTempC)} on {WeatherVisuals.DayLong(highest.Date)}."
                    : "")
                + (Gaps ? $" {N - MeasuredCount} of {N} days were not measured." : "")
                + " A table of every day follows.";
        }
    }

    private string PlotKeyLabel => $"{AriaLabel} Use Left and Right arrow keys to read each day.";

    // ── Axes ────────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>
    /// The date labels. Up to 90 days: the sparsest of daily / 2-day / weekly / fortnightly that fits,
    /// counted back from the newest day so the right edge is always labelled. Longer: the first of each
    /// month (every 2nd, 3rd or 6th when space is short), with the year on January and on the first label.
    /// </summary>
    internal static IReadOnlyList<(int Index, string Text, bool Start)> DateLabels(DateOnly from, int n, double plotWidth)
    {
        var maxLabels = Math.Max(2, (int)Math.Floor(plotWidth / 64));
        if (n <= 90)
        {
            var every = new[] { 1, 2, 7, 14, 28 }.FirstOrDefault(s => (int)Math.Ceiling(n / (double)s) <= maxLabels, 28);
            return [.. Enumerable.Range(0, n).Where(i => (n - 1 - i) % every == 0)
                .Select(i => (i, from.AddDays(i).ToString("d MMM", CultureInfo.InvariantCulture), false))];
        }

        var firsts = Enumerable.Range(0, n).Where(i => from.AddDays(i).Day == 1).ToList();
        var everyMonth = new[] { 1, 2, 3, 6 }.FirstOrDefault(k => (int)Math.Ceiling(firsts.Count / (double)k) <= maxLabels, 6);
        var shown = firsts.Where((_, k) => k % everyMonth == 0).ToList();
        return [.. shown.Select((i, k) =>
        {
            var date = from.AddDays(i);
            var month = Months[date.Month - 1];
            return (i, date.Month == 1 || k == 0 ? $"{month} {date.Year}" : month, true);
        })];
    }

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);

    // Razor reserves <text> as a control keyword, so the axis labels are emitted as raw SVG markup.
    private string YAxisMarkup => string.Concat(_ticks.Select(v =>
        $"<text x=\"{F(X0 - 10)}\" y=\"{F(Sy(v) + 4)}\" text-anchor=\"end\"{(v == 0 ? " class=\"odc-lc-axis-zero\"" : "")}>"
        + $"{Enc(WeatherVisuals.Temp((decimal)v))}</text>"));

    private string XAxisMarkup => string.Concat(DateLabels(From, N, X1 - X0).Select(l =>
    {
        // A label centred on the newest column overhangs the viewBox on a narrow plot; one that would is
        // pinned to the edge instead (the axis text advances about AxisCharWidth units per character).
        var x = Cx(l.Index);
        var width = l.Text.Length * OdsLineChart.AxisCharWidth;
        var anchor = l.Start ? (x + width > _vbW ? "end" : "start") : (x + width / 2 > _vbW ? "end" : "middle");
        if (anchor == "end") x = _vbW - 1;
        return $"<text x=\"{F(x)}\" y=\"{F(YBot + 24)}\" text-anchor=\"{anchor}\">{Enc(l.Text)}</text>";
    }));

    // ── Hover and keyboard readout (the LineChart pattern) ─────────────────────────────────────────

    private void ShowHover(int index, bool byKey)
    {
        _hover = index;
        _hoverByKey = byKey;
    }

    private void ClearHover()
    {
        _hover = null;
        _hoverByKey = false;
    }

    private void OnPlotFocus()
    {
        if (N > 0) ShowHover(_hover ?? N - 1, byKey: true);
    }

    private void OnPlotKey(KeyboardEventArgs e)
    {
        if (e.Key == "Escape") { ClearHover(); return; }
        if (N == 0 || e.Key is not ("ArrowLeft" or "ArrowRight")) return;
        var from = _hover ?? N - 1;
        ShowHover(Math.Clamp(from + (e.Key == "ArrowRight" ? 1 : -1), 0, N - 1), byKey: true);
    }

    private string TipStyle(Column c)
    {
        var anchor = c.Observed is { } o ? o.MaxTempC ?? o.MeanTempC ?? o.MinTempC : null;
        var top = (anchor is { } a ? Sy((double)a) : YTop + 24) / ViewBoxHeight * 100;
        return OdsLineChart.TipStyle(Cx(c.Index) / _vbW * 100, top);
    }

    private string TipEdge(Column c) => OdsLineChart.TipEdge(Cx(c.Index) / _vbW * 100);

    /// <summary>The live region's sentence for the day the keyboard is on.</summary>
    private string? TipText => _hover is { } h && h < N
        ? _cols[h].Observed is { } o
            ? $"{WeatherVisuals.DayLong(_cols[h].Date)}: high {WeatherVisuals.Temp(o.MaxTempC)}, low {WeatherVisuals.Temp(o.MinTempC)}, mean {WeatherVisuals.Temp(o.MeanTempC)}"
            : $"{WeatherVisuals.DayLong(_cols[h].Date)}: not measured"
        : null;
}
