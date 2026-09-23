using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace Odyssey.Client.Components;

/// <summary>
/// The parameter surface and plot geometry behind <c>OdsLineChart.razor</c> (Odyssey Design System ·
/// components/LineChart). See the markup file's header for what the card is and why the
/// reconstructed-series additions — negatives, point kinds, the text equivalent — exist.
/// </summary>
public partial class OdsLineChart
{
    /// <summary>The series, oldest → newest. Points with a null Value are skipped.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<OdsLinePoint> Series { get; set; } = [];

    /// <summary>Line + area + dot color. Default <c>var(--chart-1)</c>. Use a categorical chart token.</summary>
    [Parameter] public string Color { get; set; } = "var(--chart-1)";

    /// <summary>Plot the running total of Value instead of each point's own value.</summary>
    [Parameter] public bool Cumulative { get; set; }

    [Parameter] public string? Title { get; set; }

    /// <summary>
    /// The sub-line under the title. A <see cref="RenderFragment"/> rather than a string, so a
    /// consumer can pass a caption plus one note sentence per disclosure — mirroring the DS contract,
    /// which already types <c>sub</c> as a node. Note sentences go in
    /// <c>&lt;span class="odc-lc-note"&gt;</c>, which the sheet blocks onto its own line.
    /// </summary>
    [Parameter] public RenderFragment? Sub { get; set; }

    /// <summary>Formats the headline figure + (by default) the y-axis ticks.</summary>
    [Parameter] public Func<decimal, string> Format { get; set; } =
        static n => n.ToString("#,##0.##", CultureInfo.InvariantCulture);

    /// <summary>Compact y-axis tick formatter; falls back to <see cref="Format"/>.</summary>
    [Parameter] public Func<decimal, string>? AxisFormat { get; set; }

    /// <summary>
    /// Show a latest-vs-first delta beside the figure (mint up / coral down). Withheld when either
    /// endpoint is <see cref="OdsLinePointKind.Partial"/> — an understated endpoint makes the
    /// difference meaningless. A <see cref="OdsLinePointKind.Revalued"/> endpoint is a real movement
    /// and does not withhold it.
    /// </summary>
    [Parameter] public bool ShowDelta { get; set; }

    /// <summary>Trailing text on the delta, e.g. "all-time" or "vs 2024".</summary>
    [Parameter] public string? DeltaSuffix { get; set; }

    /// <summary>Override the headline figure (else the latest point, via <see cref="Format"/>).</summary>
    [Parameter] public string? Figure { get; set; }

    /// <summary>Render every Nth category label (the last is always shown). Default 1.</summary>
    [Parameter] public int XTickEvery { get; set; } = 1;

    /// <summary>
    /// Derive the stride from the point count instead of using <see cref="XTickEvery"/>.
    ///
    /// <para>
    /// The DS types this as a union (<c>xTickEvery: number | 'auto'</c>), which a Blazor parameter
    /// cannot express without an <c>object?</c>; two parameters keep it typed, and keeping the default
    /// on <see cref="XTickEvery"/> is what stops the existing consumers' tick spacing from changing
    /// under them.
    /// </para>
    /// </summary>
    [Parameter] public bool XTickEveryAuto { get; set; }

    /// <summary>
    /// Fill the area under the line. Anchors at zero when the value domain straddles it, at the plot
    /// floor otherwise. Default true.
    /// </summary>
    [Parameter] public bool Area { get; set; } = true;

    /// <summary>
    /// Show the marker key under the plot when the series contains Partial or Revalued points.
    /// Default true; nothing renders for an all-Normal series either way.
    /// </summary>
    [Parameter] public bool MarkLegend { get; set; } = true;

    /// <summary>
    /// Render a visually-hidden table of every point, its value and its state. <b>Opt-in</b> — off by
    /// default, so the assistive-technology output of the consumers that predate it is unchanged.
    /// </summary>
    [Parameter] public bool TextEquivalent { get; set; }

    /// <summary>Header for the text equivalent's first column.</summary>
    [Parameter] public string TextEquivalentLabel { get; set; } = "Period";

    [Parameter] public string? AriaLabel { get; set; }

    [Parameter] public string? Class { get; set; }

    /// <summary>Shown when the series has no plottable points. State the cause.</summary>
    [Parameter] public string EmptyLabel { get; set; } = "No data yet.";

    // Fall back to the visible title so an unlabeled chart still names itself to AT.
    private string EffectiveAriaLabel =>
        !string.IsNullOrEmpty(AriaLabel) ? AriaLabel
        : !string.IsNullOrEmpty(Title) ? $"{Title} — line chart"
        : "Line chart";

    private const string PartialLabel = "Understated";
    private const string RevaluedLabel = "Revalued";

    /// <summary>
    /// The text-equivalent sentence for each kind. Each state gets its own wording: a reader who
    /// cannot see the markers has to be able to tell an understated figure from a revaluation step,
    /// and those are opposites — one withholds the delta, the other does not.
    /// </summary>
    private static string KindDescription(OdsLinePointKind kind) => kind switch
    {
        OdsLinePointKind.Partial => "Understated — an account had no exchange rate for this period",
        OdsLinePointKind.Revalued => "Revalued — an estimate took effect in this period",
        _ => "Measured",
    };

    // The plot box inside the 1000 × 252 viewBox. The x-tick baseline sits at YBot + 26 = 238.
    private const double X0 = 64, X1 = 968, YTop = 28, YBot = 212;

    private readonly List<(string Label, double Value, OdsLinePointKind Kind)> _pts = [];
    private bool _single;
    private bool _straddles;
    private bool _anyMarked;
    private bool _deltaOk;
    private int _every = 1;
    private double _yMin, _yMax;
    private double[] _gridVals = [];
    private string _fillId = "";

    private string FmtAxis(decimal n) => (AxisFormat ?? Format)(n);

    protected override void OnParametersSet()
    {
        // Build the plotted points (oldest → newest), applying the running total in cumulative mode.
        // Partial and revalued points are plotted and marked, never dropped — nulling one would move
        // _pts[0]/_pts[^1] and make the delta span a window its suffix misdescribes.
        _pts.Clear();
        double acc = 0;
        foreach (var point in Series)
        {
            if (point?.Value is null) continue;
            acc += (double)point.Value.Value;
            _pts.Add((point.Label, Cumulative ? acc : (double)point.Value.Value, point.Kind));
        }

        _anyMarked = _pts.Any(p => p.Kind != OdsLinePointKind.Normal);
        _deltaOk = ShowDelta && _pts.Count > 1
                   && _pts[0].Kind != OdsLinePointKind.Partial
                   && _pts[^1].Kind != OdsLinePointKind.Partial;

        if (_pts.Count == 0) return;

        _single = _pts.Count == 1;
        var lo = _pts.Min(p => p.Value);
        var hi = _pts.Max(p => p.Value);
        var span = (hi - lo) != 0 ? hi - lo : (Math.Abs(hi) != 0 ? Math.Abs(hi) : 1);
        var pad = span * 0.18;
        // The floor clamp at 0 is kept for a series that never goes negative, so every consumer that
        // predates this renders byte-identically; a negative low drops the floor instead of pinning
        // the point below the plot area, which is where the old Math.Max put it — a net worth of
        // −827 700 landed at y ≈ 256, past both the x-tick baseline and the viewBox itself.
        _yMin = lo < 0 ? lo - pad : Math.Max(0, lo - pad);
        _yMax = hi + pad;
        _straddles = _yMin < 0 && _yMax > 0;

        // Four evenly-spaced values, as before — unless the domain straddles zero, in which case zero
        // is a gridline of its own and each half is halved. The four even values generally would NOT
        // include zero, and an unlabelled origin on a chart that crosses it is the one label a reader
        // needs most.
        _gridVals = _straddles
            ? [_yMax, _yMax / 2, 0, _yMin / 2, _yMin]
            : [_yMax, _yMin + (_yMax - _yMin) * 2 / 3, _yMin + (_yMax - _yMin) / 3, _yMin];

        _every = XTickEveryAuto ? TickEvery(_pts.Count) : (XTickEvery > 0 ? XTickEvery : 1);
        _fillId = $"odc-lc-fill-{Guid.NewGuid():N}";
    }

    /// <summary>
    /// The auto stride: start at <c>ceil(n / 8)</c>, then step up until the tail label and the last
    /// strided label are not adjacent.
    ///
    /// <para>
    /// The condition is <c>(n - 1) % every == 1</c> — the last strided index is one short of the tail,
    /// so both would be drawn side by side and overlap at caption size. Checking it once is not
    /// enough: incrementing can land on the same condition again, and a rule that merely recomputed
    /// from a smaller divisor was a no-op at <c>n = 17</c>, where <c>ceil(17/8) == ceil(17/7) == 3</c>.
    /// </para>
    /// </summary>
    internal static int TickEvery(int n)
    {
        if (n <= 1) return 1;
        var every = Math.Max(1, (int)Math.Ceiling(n / 8.0));
        var guard = 0;
        while ((n - 1) % every == 1 && guard++ < n) every += 1;
        return every;
    }

    private double Sx(int i) => _single ? (X0 + X1) / 2 : X0 + i * (X1 - X0) / (_pts.Count - 1);

    private double Sy(double v) =>
        YBot - (v - _yMin) / (_yMax - _yMin != 0 ? _yMax - _yMin : 1) * (YBot - YTop);

    /// <summary>Where the area fill closes: the zero line when the domain crosses it, the plot floor otherwise.</summary>
    private double Baseline => _straddles ? Sy(0) : YBot;

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private string LinePts => string.Join(' ', _pts.Select((p, i) => $"{Fmt(Sx(i))},{Fmt(Sy(p.Value))}"));

    private string AreaPath =>
        $"M {Fmt(Sx(0))} {Fmt(Baseline)} " +
        string.Join(' ', _pts.Select((p, i) => $"L {Fmt(Sx(i))} {Fmt(Sy(p.Value))}")) +
        $" L {Fmt(Sx(_pts.Count - 1))} {Fmt(Baseline)} Z";

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);

    // Razor reserves <text> as a control keyword, so the axis labels are emitted as raw SVG markup.
    private string YAxisMarkup => string.Concat(_gridVals.Select(v =>
        $"<text x=\"{Fmt(X0 - 12)}\" y=\"{Fmt(Sy(v) + 4)}\" text-anchor=\"end\""
        + $"{(v == 0 ? " class=\"odc-lc-axis-zero\"" : "")}>{Enc(FmtAxis((decimal)AxisRound(v)))}</text>"));

    // A domain under 10 keeps its fractions: rounding a 0–4 axis to whole numbers prints "1, 1, 3, 4"
    // for four distinct gridlines.
    private double AxisRound(double v) => Math.Abs(_yMax) < 10 ? v : Math.Round(v);

    private string XAxisMarkup => string.Concat(Enumerable.Range(0, _pts.Count)
        .Where(i => i % _every == 0 || i == _pts.Count - 1)
        .Select(i => $"<text x=\"{Fmt(Sx(i))}\" y=\"{Fmt(YBot + 26)}\" text-anchor=\"middle\">{Enc(_pts[i].Label)}</text>"));
}
