using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;

namespace Odyssey.Client.Components;

/// <summary>
/// The parameter surface and plot geometry behind <c>OdsStepChart.razor</c> (Odyssey Design System ·
/// components/StepChart). See the markup file's header for what the card is and why it differs from
/// <see cref="OdsLineChart"/>.
/// </summary>
public partial class OdsStepChart
{
    /// <summary>The entries, any order — sorted by date internally. Null values are skipped.</summary>
    [Parameter] public IReadOnlyList<OdsStepPoint> Series { get; set; } = [];

    /// <summary>
    /// Compare several histories (supersedes <see cref="Series"/>). Plotted as <b>indexed change</b> by
    /// default — see <see cref="Scale"/>. Only overlay series sharing a unit and a currency: the
    /// component cannot know that, and the legend's figures would mix.
    /// </summary>
    [Parameter] public IReadOnlyList<OdsStepLine>? Lines { get; set; }

    /// <summary>
    /// How the y-axis is plotted. <see cref="OdsStepScale.Auto"/> indexes a comparison and shows real
    /// figures for a single series.
    /// </summary>
    [Parameter] public OdsStepScale Scale { get; set; } = OdsStepScale.Auto;

    /// <summary>Line + area + dot colour for the single-series form. Default <c>var(--chart-1)</c>.</summary>
    [Parameter] public string Color { get; set; } = "var(--chart-1)";

    [Parameter] public string? Title { get; set; }

    /// <summary>The sub-line under the title — a node, so a consumer can pass disclosures.</summary>
    [Parameter] public RenderFragment? Sub { get; set; }

    /// <summary>Formats the headline figure, the delta, the legend and (absent <see cref="AxisFormat"/>) the ticks.</summary>
    [Parameter] public Func<decimal, string> Format { get; set; } =
        static n => n.ToString("#,##0.##", CultureInfo.InvariantCulture);

    /// <summary>Compact y-axis tick formatter for an absolute axis; falls back to <see cref="Format"/>.</summary>
    [Parameter] public Func<decimal, string>? AxisFormat { get; set; }

    /// <summary>
    /// Show the change beside the figure — the in-force entry vs <b>the one before it</b>, not vs the
    /// first. Withheld where nothing has changed yet, and when several lines are plotted.
    /// </summary>
    [Parameter] public bool ShowDelta { get; set; }

    /// <summary>Show the head's figure + delta at all. Default true.</summary>
    [Parameter] public bool ShowFigure { get; set; } = true;

    /// <summary>Trailing text on the delta, e.g. "vs Mar ’26".</summary>
    [Parameter] public string? DeltaSuffix { get; set; }

    /// <summary>Signed (by sign) or Neutral (grey) — Neutral whenever the figure carries a colour of its own.</summary>
    [Parameter] public OdsDeltaTone DeltaTone { get; set; } = OdsDeltaTone.Signed;

    /// <summary>Override the headline figure (else the value IN FORCE, via <see cref="Format"/>).</summary>
    [Parameter] public string? Figure { get; set; }

    /// <summary>Colour for the headline figure — a hue the product already assigns (a direction, a kind).</summary>
    [Parameter] public string? FigureColor { get; set; }

    /// <summary>Fill under the in-force part of the line. Single-series only. Default true.</summary>
    [Parameter] public bool Area { get; set; } = true;

    /// <summary>The chart's own controls, in the title's place (a series picker).</summary>
    [Parameter] public RenderFragment? Controls { get; set; }

    /// <summary>A control that changes how the chart reads (an axis toggle), at the head's right.</summary>
    [Parameter] public RenderFragment? ControlsEnd { get; set; }

    /// <summary>Text on the present-day marker.</summary>
    [Parameter] public string NowLabel { get; set; } = "Today";

    /// <summary>Visually-hidden table of every entry, its value and its state. Opt-in.</summary>
    [Parameter] public bool TextEquivalent { get; set; }

    [Parameter] public string TextEquivalentLabel { get; set; } = "Effective from";

    [Parameter] public string? AriaLabel { get; set; }

    [Parameter] public string? Class { get; set; }

    /// <summary>Shown when the series has no plottable entries. State the cause.</summary>
    [Parameter] public string EmptyLabel { get; set; } = "No entries yet.";

    /// <summary>The present, for the today marker and the in-force split. Defaults to now (UTC); tests pin it.</summary>
    [Parameter] public DateTime? Now { get; set; }

    // The plot box inside the 1000 × 252 viewBox — LineChart's, so the two cards read as one.
    private const double X0 = 64, X1 = 968, YTop = 28, YBot = 212;

    internal sealed record Plot(
        string Id,
        string Label,
        string Color,
        IReadOnlyList<(DateOnly Date, double Value, string? PointId)> Pts,
        int InForce,
        string Solid,
        string Dashed,
        string AreaPath,
        double Dodge);

    private readonly List<Plot> _plots = [];
    private bool _multi;
    private bool _indexed;
    private double _t0, _tMax, _now, _yMin, _yMax;
    private double[] _gridVals = [];
    private string _fillId = "";

    private bool HasData => _plots.Count > 0;
    private Plot? Primary => _plots.Count > 0 ? _plots[0] : null;

    private bool FigureShown => ShowFigure && !_multi && (Figure is not null || HasData);

    private (double Value, double? Prev)? Headline
    {
        get
        {
            if (Primary is not { } p) return null;
            var last = p.Pts[p.InForce].Value;
            return (last, p.InForce > 0 ? p.Pts[p.InForce - 1].Value : null);
        }
    }

    private static double Days(DateOnly d) => d.DayNumber;

    protected override void OnParametersSet()
    {
        var now = Now ?? DateTime.UtcNow;
        // Fractional day: a point dated today is already in force, one dated tomorrow is not.
        _now = DateOnly.FromDateTime(now).DayNumber + now.TimeOfDay.TotalDays;

        // Lines says what to plot; the COUNT says how to read it — one line keeps a headline figure and
        // a real-value axis whether it arrived as Series or as a one-entry Lines.
        IEnumerable<OdsStepLine> source = Lines is { Count: > 0 }
            ? Lines
            : [new OdsStepLine { Id = "_", Label = Title ?? "", Color = Color, Points = Series }];
        var sets = source
            .Select((l, i) => (Id: string.IsNullOrEmpty(l.Id) ? $"l{i}" : l.Id, l.Label, Color: l.Color ?? Color, Pts: Sort(l.Points)))
            .Where(l => l.Pts.Count > 0)
            .ToList();

        _plots.Clear();
        _multi = sets.Count > 1;
        if (sets.Count == 0) return;

        _indexed = Scale == OdsStepScale.Auto ? _multi : Scale == OdsStepScale.Indexed;

        var allDays = sets.SelectMany(s => s.Pts).Select(p => Days(p.Date)).ToList();
        _t0 = allDays.Min();
        // The axis runs to whichever is later: the present, or the furthest scheduled entry. One day
        // of span is the floor, so a history whose only entry is dated today still has a plot.
        _tMax = Math.Max(Math.Max(_now, allDays.Max()), _t0 + 1);

        var vals = sets.SelectMany(s => s.Pts.Select(p => Plotted(s.Pts, p.Value))).ToList();
        double lo = vals.Min(), hi = vals.Max();
        if (lo == hi)
        {
            // A set that has never changed has no range, so the band is invented — in whatever unit is
            // being PLOTTED: indexed fractions need a visible band, a rate (a fraction) half a point, a
            // rent a proportional one rather than fake precision.
            var band = _indexed ? 0.02 : FlatBand(lo);
            lo -= band;
            hi += band;
        }
        var pad = (hi - lo) * 0.18;
        // No zero clamp for data that goes negative; an all-positive set is floored at zero rather than
        // padded into a negative region the value could never occupy.
        _yMin = lo >= 0 ? Math.Max(0, lo - pad) : lo - pad;
        _yMax = hi + pad;
        _gridVals = [_yMax, _yMin + (_yMax - _yMin) * 2 / 3, _yMin + (_yMax - _yMin) / 3, _yMin];

        var nowX = Sx(_now);
        for (var i = 0; i < sets.Count; i++)
        {
            var s = sets[i];
            var walk = new List<(double X, double Y)>();
            for (var j = 0; j < s.Pts.Count; j++)
            {
                var px = Sx(Days(s.Pts[j].Date));
                var py = Sy(Plotted(s.Pts, s.Pts[j].Value));
                if (j == 0) walk.Add((px, py));
                else
                {
                    walk.Add((px, walk[^1].Y));
                    walk.Add((px, py));
                }
            }
            walk.Add((Sx(_tMax), walk[^1].Y));

            var solid = Clip(walk, nowX, keepBelow: true);
            var dashed = Clip(walk, nowX, keepBelow: false);
            var area = solid.Count > 1
                ? $"{Path(solid)} L {F(solid[^1].X)} {F(YBot)} L {F(solid[0].X)} {F(YBot)} Z"
                : "";
            // Coincident strokes: indexed mode starts EVERY series at 0%, so shared spans are the
            // normal case. Every line is fanned by a constant hair — under the stroke width — so each
            // colour stays findable while no value the axis could read moves.
            var dodge = _multi ? (i - (sets.Count - 1) / 2.0) * 2.6 : 0;
            _plots.Add(new Plot(s.Id, s.Label, s.Color, s.Pts, InForceIndex(s.Pts), Path(solid), Path(dashed), area, dodge));
        }

        _fillId = $"odc-sc-fill-{Guid.NewGuid():N}";
    }

    private static List<(DateOnly Date, double Value, string? PointId)> Sort(IReadOnlyList<OdsStepPoint>? points) =>
        (points ?? [])
            .Where(p => p?.Value is not null)
            .OrderBy(p => p.Date)
            .Select(p => (p.Date, (double)p.Value!.Value, p.Id))
            .ToList();

    /// <summary>
    /// The one answer to "what does this cost": the latest entry that has already taken effect. An
    /// entirely-future series is represented by its soonest entry.
    /// </summary>
    private int InForceIndex(IReadOnlyList<(DateOnly Date, double Value, string? PointId)> pts)
    {
        var i = pts.Count(p => Days(p.Date) <= _now) - 1;
        return i < 0 ? 0 : i;
    }

    private bool IsScheduled(DateOnly d) => Days(d) > _now;

    /// <summary>A series' move from its own first entry — what indexed mode plots and every legend row states.</summary>
    private static double MoveOf(IReadOnlyList<(DateOnly Date, double Value, string? PointId)> pts, double v)
    {
        var baseValue = pts[0].Value;
        return baseValue != 0 ? (v - baseValue) / Math.Abs(baseValue) : 0;
    }

    // A zero first entry has no percentage change, so it is plotted absolutely — better than dividing by zero.
    private double Plotted(IReadOnlyList<(DateOnly Date, double Value, string? PointId)> pts, double v) =>
        !_indexed ? v : pts[0].Value != 0 ? MoveOf(pts, v) : v;

    /// <summary>
    /// The half-band invented around a never-changed ABSOLUTE value, which has no range of its own. It
    /// is proportional, because a fixed ±0.005 is half a point around a rate (right, since rates are
    /// stored as fractions below 1) and fake precision around a rent. Shared with the account rate
    /// chart so the two cannot drift.
    /// </summary>
    internal static double FlatBand(double value) =>
        Math.Abs(value) >= 1 ? Math.Abs(value) * 0.05 : 0.005;

    /// <summary>Signed whole percent; the sign comes from the ROUNDED magnitude, so a hair above zero never prints "+0%".</summary>
    internal static string FormatIndexed(double v)
    {
        var n = (int)Math.Round(Math.Abs(v) * 100, MidpointRounding.AwayFromZero);
        var sign = n == 0 ? "" : v > 0 ? "+" : "−";
        return $"{sign}{n}%";
    }

    private string Tick(double v) =>
        _indexed ? FormatIndexed(v) : (AxisFormat ?? Format)((decimal)(Math.Abs(_yMax) < 10 ? v : Math.Round(v)));

    private string LegendMove(Plot p)
    {
        if (p.Pts.Count < 2) return "no changes yet";
        var cur = p.Pts[p.InForce].Value;
        if (_indexed) return FormatIndexed(MoveOf(p.Pts, cur));
        var d = cur - p.Pts[0].Value;
        return $"{(d > 0 ? "+" : d < 0 ? "−" : "")}{Format((decimal)Math.Abs(d))}";
    }

    private string DeltaText(double d) =>
        $"{(d >= 0 ? "+" : "−")}{Format((decimal)Math.Abs(d))}{(string.IsNullOrEmpty(DeltaSuffix) ? "" : " " + DeltaSuffix)}";

    private string DeltaClass(double d) =>
        DeltaTone == OdsDeltaTone.Neutral ? "odc-lc-delta neutral" : d >= 0 ? "odc-lc-delta income" : "odc-lc-delta expense";

    private string StateOf(Plot p, int i) =>
        IsScheduled(p.Pts[i].Date) ? "Scheduled" : i == p.InForce ? "In force" : "Superseded";

    private string EffectiveAriaLabel =>
        !string.IsNullOrEmpty(AriaLabel) ? AriaLabel
        : _multi ? $"{string.Join(", ", _plots.Select(p => p.Label).Where(l => !string.IsNullOrEmpty(l)))} — values over time"
        : !string.IsNullOrEmpty(Title) ? $"{Title} — value over time"
        : "Value over time";

    private double Sx(double t) => X0 + (t - _t0) / (_tMax - _t0) * (X1 - X0);

    private double Sy(double v) => YBot - (v - _yMin) / (_yMax - _yMin != 0 ? _yMax - _yMin : 1) * (YBot - YTop);

    private double NowX => Sx(_now);

    internal static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Path(List<(double X, double Y)> pts) =>
        pts.Count == 0 ? "" : "M " + string.Join(" L ", pts.Select(p => $"{F(p.X)} {F(p.Y)}"));

    /// <summary>
    /// Clip an axis-aligned polyline to x ≤ bound (or x ≥ bound), interpolating the crossing so the
    /// solid/dashed split lands exactly on the now marker.
    /// </summary>
    internal static List<(double X, double Y)> Clip(IReadOnlyList<(double X, double Y)> pts, double bound, bool keepBelow)
    {
        var output = new List<(double X, double Y)>();
        for (var i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var inside = keepBelow ? p.X <= bound : p.X >= bound;
            if (i > 0)
            {
                var prev = pts[i - 1];
                var prevInside = keepBelow ? prev.X <= bound : prev.X >= bound;
                if (inside != prevInside)
                {
                    output.Add(prev.X == p.X
                        ? (bound, inside ? prev.Y : p.Y)
                        : (bound, prev.Y + (p.Y - prev.Y) * ((bound - prev.X) / (p.X - prev.X))));
                }
            }
            if (inside) output.Add(p);
        }
        return output;
    }

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);

    private static string MonY(DateOnly d) =>
        d.ToString("MMM", CultureInfo.InvariantCulture) + " ’" + (d.Year % 100).ToString("00", CultureInfo.InvariantCulture);

    // Razor reserves <text> as a control keyword, so the axis labels are emitted as raw SVG markup.
    private string YAxisMarkup => string.Concat(_gridVals.Select(v =>
        $"<text x=\"{F(X0 - 12)}\" y=\"{F(Sy(v) + 4)}\" text-anchor=\"end\">{Enc(Tick(v))}</text>"));

    /// <summary>One label per change, dropped where two would collide, never within reach of the now label.</summary>
    private string XAxisMarkup
    {
        get
        {
            var sb = new StringBuilder();
            var lastX = double.NegativeInfinity;
            var nowX = NowX;
            foreach (var date in _plots.SelectMany(p => p.Pts).Select(p => p.Date).Distinct().OrderBy(d => d))
            {
                var px = Sx(Days(date));
                if (px - lastX < 64 || Math.Abs(px - nowX) < 48) continue;
                sb.Append($"<text x=\"{F(px)}\" y=\"{F(YBot + 26)}\" text-anchor=\"middle\">{Enc(MonY(date))}</text>");
                lastX = px;
            }
            return sb.ToString();
        }
    }

    private string NowMarkup
    {
        get
        {
            var nowX = NowX;
            var anchor = nowX > (X0 + X1) / 2 ? "end" : "start";
            return $"<text class=\"odc-lc-axis odc-sc-nowlabel\" x=\"{F(Math.Min(nowX + 6, X1))}\" y=\"{F(YTop - 18)}\" text-anchor=\"{anchor}\">{Enc(NowLabel)}</text>";
        }
    }

    private string DotsMarkup(Plot p)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < p.Pts.Count; i++)
        {
            var cx = F(Sx(Days(p.Pts[i].Date)));
            var cy = F(Sy(Plotted(p.Pts, p.Pts[i].Value)));
            var color = Enc(p.Color);
            if (IsScheduled(p.Pts[i].Date))
                sb.Append($"<circle cx=\"{cx}\" cy=\"{cy}\" r=\"4\" fill=\"var(--mud-palette-surface)\" stroke=\"{color}\" stroke-width=\"1.8\"></circle>");
            else
                sb.Append($"<circle cx=\"{cx}\" cy=\"{cy}\" r=\"{(i == p.InForce ? 4 : 3)}\" fill=\"{color}\"></circle>");
        }
        return sb.ToString();
    }

    private static string? DodgeTransform(Plot p) =>
        p.Dodge != 0 ? $"translate(0 {p.Dodge.ToString("0.00", CultureInfo.InvariantCulture)})" : null;
}
