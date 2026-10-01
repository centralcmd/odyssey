using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Odyssey.Client.Components;

/// <summary>
/// The parameter surface and plot geometry behind <c>OdsLineChart.razor</c> (Odyssey Design System ·
/// components/LineChart). See the markup file's header for what the card is and why the
/// reconstructed-series additions — negatives, point kinds, the text equivalent — exist.
/// </summary>
public partial class OdsLineChart : IAsyncDisposable
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

    /// <summary>
    /// Show the headline figure (and delta) in the head. Default true. Turn it off when
    /// <see cref="Legend"/> carries the figure.
    /// </summary>
    [Parameter] public bool ShowFigure { get; set; } = true;

    /// <summary>
    /// A ledger row under the plot, as <c>OdsStepChart</c>'s: swatch · name · value in force · change
    /// since the first point (plus <see cref="DeltaSuffix"/>). The change is withheld when an endpoint
    /// is <see cref="OdsLinePointKind.Partial"/>. The marker key joins this row instead of its own.
    /// Default false.
    /// </summary>
    [Parameter] public bool Legend { get; set; }

    /// <summary>
    /// Whether the legend row states the change since the first point. Default true, as in the
    /// design. A Blazor-side addition for a caller that withholds the change for a reason of its own
    /// beyond an understated endpoint (the dashboard's: an endpoint that did not contribute).
    /// </summary>
    [Parameter] public bool LegendShowsChange { get; set; } = true;

    /// <summary>Name in the legend row. Defaults to <see cref="Title"/>.</summary>
    [Parameter] public string? LegendLabel { get; set; }

    /// <summary>
    /// Controls rendered above the headline figure, right of the head — e.g. a settings button or an
    /// interval segmented control. Also shown on an empty series.
    /// </summary>
    [Parameter] public RenderFragment? ControlsEnd { get; set; }

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

    /// <summary>
    /// The text-equivalent table's State text for a <c>Partial</c> point. <c>null</c> keeps the default
    /// wording; a caller whose understatement has a different cause (e.g. a property rather than an
    /// account, issue #215) passes its own so the table agrees with the note beside the chart.
    /// </summary>
    [Parameter] public string? PartialDescription { get; set; }

    /// <summary>The text-equivalent table's State text for a <c>Revalued</c> point; <c>null</c> keeps the default.</summary>
    [Parameter] public string? RevaluedDescription { get; set; }

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

    private static string KindLabel(OdsLinePointKind kind) =>
        kind == OdsLinePointKind.Partial ? PartialLabel : RevaluedLabel;

    /// <summary>
    /// The legend's change figure: signed, since the first point, withheld when either endpoint is
    /// understated — the same rule the head's delta follows.
    /// </summary>
    private string LegendChange
    {
        get
        {
            if (_pts[0].Kind == OdsLinePointKind.Partial || _pts[^1].Kind == OdsLinePointKind.Partial)
                return "change withheld";
            var d = _pts[^1].Value - _pts[0].Value;
            var sign = d > 0 ? "+" : d < 0 ? "−" : "";
            return $"{sign}{Format((decimal)Math.Abs(d))}{(string.IsNullOrEmpty(DeltaSuffix) ? "" : " " + DeltaSuffix)}";
        }
    }

    // The index of the hovered point; null when the pointer is off the plot and it is not focused.
    private int? _hover;

    // Whether the readout was opened from the keyboard, which is the only time the live region speaks.
    private bool _hoverByKey;

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
        if (_pts.Count > 0)
            ShowHover(_hover ?? _pts.Count - 1, byKey: true);
    }

    /// <summary>
    /// Left / Right step the keyboard readout; Escape closes it. Home / End and Up / Down are left to
    /// the page, since preventing their default cannot be made conditional on the key (the handler
    /// runs after the default is decided).
    /// </summary>
    private void OnPlotKey(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            ClearHover();
            return;
        }
        if (_pts.Count == 0 || e.Key is not ("ArrowLeft" or "ArrowRight"))
            return;
        var from = _hover ?? _pts.Count - 1;
        ShowHover(Math.Clamp(from + (e.Key == "ArrowRight" ? 1 : -1), 0, _pts.Count - 1), byKey: true);
    }

    /// <summary>The plot's name as a keyboard target: what it is, and how to read it.</summary>
    private string PlotKeyLabel => $"{EffectiveAriaLabel}. Use Left and Right arrow keys to read each point.";

    /// <summary>The change from the previous point, as the readout and the table state it; "—" for the first.</summary>
    internal string ChangeText(int i)
    {
        if (i == 0) return "—";
        var d = _pts[i].Value - _pts[i - 1].Value;
        return d == 0 ? "No change" : $"{(d > 0 ? "+" : "−")}{Format((decimal)Math.Abs(d))}";
    }

    /// <summary>The change as a spoken clause: nothing for the first point, "no change", or "change +50".</summary>
    internal static string SpokenChange(string changeText, int index) =>
        index == 0 ? "" : changeText == "No change" ? ", no change" : $", change {changeText}";

    /// <summary>The live region's sentence for the point the keyboard is on.</summary>
    private string? TipText => _hover is { } h && h < _pts.Count
        ? $"{_pts[h].Label}{(_pts[h].Kind == OdsLinePointKind.Normal ? "" : $", {KindLabel(_pts[h].Kind)}")}: "
          + $"{Format((decimal)_pts[h].Value)}{SpokenChange(ChangeText(h), h)}"
        : null;

    // Half the width of a point's hover column — the whole plot for a single point.
    private double HitHalfWidth => _single ? (X1 - X0) / 2 : (X1 - X0) / (_pts.Count - 1) / 2;

    /// <summary>Pins the readout to the near edge within 14% of either side, so it never clips.</summary>
    internal static string TipEdge(double leftPercent) =>
        leftPercent < 14 ? " start" : leftPercent > 86 ? " end" : "";

    internal static string TipStyle(double leftPercent, double topPercent) =>
        $"left:{leftPercent.ToString("0.##", CultureInfo.InvariantCulture)}%;top:{topPercent.ToString("0.##", CultureInfo.InvariantCulture)}%";
    private const string RevaluedLabel = "Revalued";

    /// <summary>
    /// The text-equivalent sentence for each kind. Each state gets its own wording: a reader who
    /// cannot see the markers has to be able to tell an understated figure from a revaluation step,
    /// and those are opposites — one withholds the delta, the other does not.
    /// </summary>
    private string KindDescription(OdsLinePointKind kind) => kind switch
    {
        OdsLinePointKind.Partial => PartialDescription ?? DefaultPartialDescription,
        OdsLinePointKind.Revalued => RevaluedDescription ?? DefaultRevaluedDescription,
        _ => "Measured",
    };

    internal const string DefaultPartialDescription = "Understated — an account had no exchange rate for this period";
    internal const string DefaultRevaluedDescription = "Revalued — an estimate took effect in this period";

    // The plot box inside the W × 252 viewBox. The x-tick baseline sits at YBot + 26 = 238. The
    // left edge is not fixed: it widens past DefaultX0 when a y label would not fit (AxisGutter).
    // The width W is the plot's measured pixel width (issue #274), so one user unit is one pixel and
    // the 10-unit axis text renders at a true --fs-micro at any card width.
    private const double YTop = 28, YBot = 212;
    private double X0 => _x0;
    private double X1 => _vbW - PlotRightInset;
    private double _x0 = DefaultX0;
    private double _vbW = DefaultViewBoxWidth;

    /// <summary>The viewBox width before the plot has been measured (prerender, or no JS runtime).</summary>
    internal const double DefaultViewBoxWidth = 1000;

    /// <summary>The fixed viewBox height; with the width tracking the plot, also its pixel height.</summary>
    internal const double ViewBoxHeight = 252;

    /// <summary>The gap between the plot's right edge and the viewBox's.</summary>
    internal const double PlotRightInset = 32;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    private ElementReference _plotRef;
    private PlotWidthObserver? _widthObserver;

    private string ViewBox => $"0 0 {Fmt(_vbW)} {Fmt(ViewBoxHeight)}";

    /// <summary>A hover readout's left edge, as a percentage of the plot width.</summary>
    private double TipLeftPercent(int i) => Sx(i) / _vbW * 100;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _widthObserver ??= new PlotWidthObserver(JS, OnPlotWidth);
        await _widthObserver.SyncAsync(_pts.Count > 0 ? _plotRef : null);
    }

    private Task OnPlotWidth(double width) => InvokeAsync(() =>
    {
        var w = Math.Round(width);
        if (w <= 0 || w == _vbW) return;
        _vbW = w;
        _every = ResolveEvery();
        StateHasChanged();
    });

    public async ValueTask DisposeAsync()
    {
        if (_widthObserver is not null)
            await _widthObserver.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>The plot's left edge when every y label fits the default gutter.</summary>
    internal const double DefaultX0 = 64;

    /// <summary>The gap between a y label's right end and the plot.</summary>
    internal const double AxisLabelGap = 12;

    /// <summary>
    /// The advance of one axis character, in viewBox units: <c>.odc-lc-axis</c> is 10-unit monospace,
    /// whose glyphs advance about 0.6em. Rounded up so an estimate errs toward a wider gutter.
    /// </summary>
    internal const double AxisCharWidth = 6.2;

    /// <summary>Clear space kept left of the widest label, inside the viewBox.</summary>
    internal const double AxisLabelMargin = 4;

    /// <summary>
    /// The plot's left edge for these y labels: <see cref="DefaultX0"/>, or wider when the longest
    /// label would otherwise start left of the viewBox and be clipped. A compact money tick carries its
    /// currency code (<c>17.23M NOK</c>, ten characters), which the fixed 52-unit gutter cut to
    /// <c>7.23M NOK</c>. Shared with <see cref="OdsStepChart"/>, which draws the same card.
    /// </summary>
    internal static double AxisGutter(IEnumerable<string> labels)
    {
        var longest = labels.Select(l => l.Length).DefaultIfEmpty(0).Max();
        return Math.Max(DefaultX0, Math.Ceiling(AxisLabelMargin + longest * AxisCharWidth + AxisLabelGap));
    }

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

        if (_hover >= _pts.Count) ClearHover();
        _x0 = DefaultX0;
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

        _x0 = AxisGutter(_gridVals.Select(YLabel));
        _every = ResolveEvery();
        _fillId = $"odc-lc-fill-{Guid.NewGuid():N}";
    }

    private int ResolveEvery() => XTickEveryAuto
        ? TickEvery(_pts.Count, X1 - X0, _pts.Count == 0 ? 0 : _pts.Max(p => p.Label.Length) * AxisCharWidth)
        : (XTickEvery > 0 ? XTickEvery : 1);

    /// <summary>The clear space kept between two neighbouring x labels.</summary>
    internal const double XLabelGap = 8;

    /// <inheritdoc cref="TickEvery(int, double, double)"/>
    internal static int TickEvery(int n) => TickEvery(n, 0, 0);

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
    /// <para>
    /// Eight labels is a count, not a width: since the plot is sized in pixels (issue #274), eight
    /// <c>Apr '16</c>-wide labels on a phone-width card overlapped one another. So when the plot width
    /// and the widest label are known, the stride also never puts two drawn labels — the tail included
    /// — closer than a label's width plus <see cref="XLabelGap"/>. Unmeasured (prerender, no JS), it
    /// falls back to the count rule alone.
    /// </para>
    /// </summary>
    internal static int TickEvery(int n, double plotWidth, double labelWidth)
    {
        if (n <= 1) return 1;
        var every = Math.Max(1, (int)Math.Ceiling(n / 8.0));
        var step = plotWidth > 0 && labelWidth > 0 ? plotWidth / (n - 1) : 0;
        var need = labelWidth + XLabelGap;
        if (step > 0)
            every = Math.Max(every, (int)Math.Ceiling(need / step));

        bool Crowded(int e)
        {
            var tail = (n - 1) % e;
            return tail == 1 || (step > 0 && tail != 0 && tail * step < need);
        }

        // Past n - 1 only the first and the tail label are left, and nothing further can help.
        while (every < n - 1 && Crowded(every)) every += 1;
        return Math.Min(every, Math.Max(1, n - 1));
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
        $"<text x=\"{Fmt(X0 - AxisLabelGap)}\" y=\"{Fmt(Sy(v) + 4)}\" text-anchor=\"end\""
        + $"{(v == 0 ? " class=\"odc-lc-axis-zero\"" : "")}>{Enc(YLabel(v))}</text>"));

    private string YLabel(double v) => FmtAxis((decimal)AxisRound(v));

    // A domain under 10 keeps its fractions: rounding a 0–4 axis to whole numbers prints "1, 1, 3, 4"
    // for four distinct gridlines.
    private double AxisRound(double v) => Math.Abs(_yMax) < 10 ? v : Math.Round(v);

    private string XAxisMarkup => string.Concat(Enumerable.Range(0, _pts.Count)
        .Where(i => i % _every == 0 || i == _pts.Count - 1)
        .Select(i => $"<text x=\"{Fmt(Sx(i))}\" y=\"{Fmt(YBot + 26)}\" text-anchor=\"middle\">{Enc(_pts[i].Label)}</text>"));
}
