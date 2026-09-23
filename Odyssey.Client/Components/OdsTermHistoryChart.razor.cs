using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace Odyssey.Client.Components;

/// <summary>
/// The selection, colour-lease and axis rules behind <c>OdsTermHistoryChart.razor</c> (Odyssey Design
/// System · components/TermHistoryChart). See the markup file's header for the four rules.
/// </summary>
public partial class OdsTermHistoryChart
{
    /// <summary>
    /// The DS default palette: chart-1/2/4/6 — it excludes the hues equal to income/expense, so a
    /// fallback never asserts the opposite direction. Its length is the comparison cap.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPalette =
        ["var(--chart-1)", "var(--chart-2)", "var(--chart-4)", "var(--chart-6)"];

    /// <summary>Ordered histories; the FIRST is the default selection. Series without points are ignored.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<OdsTermHistorySeries> Series { get; set; } = [];

    /// <summary>Picker label, standing in for the card title.</summary>
    [Parameter] public string PickerLabel { get; set; } = "Terms";

    /// <summary>Picker glyph — a typeset mark the icon font has no ligature for.</summary>
    [Parameter] public string Glyph { get; set; } = "§";

    /// <summary>Fallback hues for a line whose own colour is taken; its length is also the cap.</summary>
    [Parameter] public IReadOnlyList<string> Palette { get; set; } = DefaultPalette;

    /// <summary>Header of the date column in the text-equivalent table.</summary>
    [Parameter] public string TextEquivalentLabel { get; set; } = "Effective from";

    [Parameter] public string? Class { get; set; }

    /// <summary>The present, forwarded to the chart. Tests pin it.</summary>
    [Parameter] public DateTime? Now { get; set; }

    private const string IndexedValue = "indexed";
    private const string AbsoluteValue = "absolute";

    private static readonly IReadOnlyList<OdsSegmentedOption> ScaleOptions =
    [
        new() { Value = IndexedValue, Label = "Change" },
        new() { Value = AbsoluteValue, Label = "Value" },
    ];

    private static string DefaultFormat(decimal n) => n.ToString("#,##0.##", CultureInfo.InvariantCulture);

    // Null until the reader chooses: the chart's Auto then indexes a comparison and shows real figures
    // for one line. A choice sticks.
    private List<string>? _sel;
    private OdsStepScale? _scale;

    // Colour leases, keyed by series key, for the life of the view.
    private readonly Dictionary<string, string?> _leases = new(StringComparer.Ordinal);

    private List<OdsTermHistorySeries> _list = [];
    private List<string> _active = [];
    private List<OdsTermHistorySeries> _picked = [];
    private List<OdsStepLine> _lines = [];
    private List<OdsOption> _options = [];

    private string ScaleValue => _scale switch
    {
        OdsStepScale.Indexed => IndexedValue,
        OdsStepScale.Absolute => AbsoluteValue,
        _ => _picked.Count > 1 ? IndexedValue : AbsoluteValue,
    };

    protected override void OnParametersSet() => Resolve();

    private List<string> Valid(IEnumerable<string>? keys) =>
        (keys ?? []).Where(k => _list.Any(x => x.Key == k)).ToList();

    private void Resolve()
    {
        _list = Series.Where(s => s is { Points.Count: > 0 }).ToList();
        if (_list.Count == 0)
        {
            _active = [];
            _picked = [];
            _lines = [];
            _options = [];
            return;
        }

        var chosen = Valid(_sel);
        _active = chosen.Count > 0 ? chosen : [_list[0].Key];
        _picked = _active
            .Select(k => _list.First(x => x.Key == k))
            .Take(Math.Max(1, Palette.Count))
            .ToList();

        // Renew held leases in selection order, then give newcomers their own hue if free, else the
        // first free palette colour.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var x in _picked)
        {
            if (_leases.GetValueOrDefault(x.Key) is not { } held) continue;
            if (!taken.Add(held)) _leases[x.Key] = null;
        }
        var fallback = Palette.Count > 0 ? Palette[0] : "var(--chart-1)";
        foreach (var x in _picked)
        {
            if (_leases.GetValueOrDefault(x.Key) is not null) continue;
            var own = x.Color ?? fallback;
            var hue = !taken.Contains(own) ? own : (Palette.FirstOrDefault(h => !taken.Contains(h)) ?? fallback);
            _leases[x.Key] = hue;
            taken.Add(hue);
        }

        _lines = _picked
            .Select(x => new OdsStepLine { Id = x.Key, Label = x.Label, Color = ColorOf(x), Points = x.Points })
            .ToList();

        // The dash means "this is the line", so only a selected row carries its colour.
        _options = _list
            .Select(x => new OdsOption(x.Key, string.Join(" ", new[] { x.Label, x.Value, x.ToneLabel }.Where(t => !string.IsNullOrEmpty(t))))
            {
                Icon = "remove",
                IconColor = _active.Contains(x.Key) ? ColorOf(x) : null,
            })
            .ToList();
    }

    private string ColorOf(OdsTermHistorySeries x) =>
        _leases.GetValueOrDefault(x.Key) ?? x.Color ?? (Palette.Count > 0 ? Palette[0] : "var(--chart-1)");

    /// <summary>
    /// The picker's rules, applied to what the checkbox list proposes: a removal is honoured unless it
    /// empties the plot; an addition from another group replaces the selection; an addition past the
    /// cap is refused.
    /// </summary>
    internal static List<string> NextSelection(
        IReadOnlyList<string> current,
        IReadOnlyCollection<string> proposed,
        IReadOnlyList<OdsTermHistorySeries> list,
        int cap)
    {
        var added = proposed.Where(k => !current.Contains(k)).ToList();
        if (added.Count == 0)
            return proposed.Count > 0 ? current.Where(proposed.Contains).ToList() : current.Take(1).ToList();

        var x = list.First(y => y.Key == added[^1]);
        var first = list.First(y => y.Key == current[0]);
        if ((x.Group ?? "") != (first.Group ?? ""))
            return [x.Key];
        if (current.Count >= cap)
            return [.. current];
        return [.. current, x.Key];
    }

    private void OnPick(IReadOnlyCollection<string> next)
    {
        _sel = NextSelection(_active, next, _list, Math.Max(1, Palette.Count));
        Resolve();
    }

    private void OnScale(string value)
    {
        _scale = value == IndexedValue ? OdsStepScale.Indexed : OdsStepScale.Absolute;
    }
}
