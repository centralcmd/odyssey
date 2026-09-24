using System.Globalization;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The term history chart's series, for BOTH owners of the Term table. An account and a contract
/// carry the same shape of history — named series, each with its own dated entries — so the design
/// system gives them one chart (the series chooser above the history table), and this is its one
/// builder: two copies would let an account's line and a contract's line word or colour the same term
/// differently.
/// </summary>
public static class TermChartSeries
{
    /// <summary>
    /// Resolves an owner's terms into the chart's plain series — one per labelled series,
    /// ordered by whose LATEST entry is most recent (a scheduled one counts: it is the change the
    /// reader came to look at), so the first is the default selection.
    /// </summary>
    /// <remarks>
    /// Everything a series STATES comes from its entry in force — the newest already taken effect, or
    /// for an entirely-scheduled series its earliest — so the picker, the legend and the tiles above
    /// agree. Two series may share an axis only if they are the same unit and, for money, the same
    /// currency, which is what <see cref="OdsTermHistorySeries.Group"/> carries — and within one
    /// series only the entries measured like the in-force one are plotted.
    /// </remarks>
    public static List<OdsTermHistorySeries> Build(
        IReadOnlyList<ExistingTerm> terms, DateTime asOf, Func<decimal, string?, string> formatMoney) =>
        terms
            .GroupBy(t => TermLabel.Key(t.Label))
            .Select(group =>
            {
                var entries = group.OrderBy(t => t.EffectiveFrom).ThenBy(t => t.CreatedAtUtc).ToList();
                var inForce = entries.LastOrDefault(t => t.EffectiveFrom.Date <= asOf) ?? entries[0];
                return (Latest: entries[^1].EffectiveFrom, Series: ToSeries(group.Key, entries, inForce, formatMoney));
            })
            .OrderByDescending(x => x.Latest)
            .Select(x => x.Series)
            .ToList();

    private static OdsTermHistorySeries ToSeries(
        string? labelKey,
        List<ExistingTerm> entries,
        ExistingTerm inForce,
        Func<decimal, string?, string> formatMoney)
    {
        var pct = inForce.ValueUnit == TermValueUnit.Percentage;
        var currency = inForce.CurrencyCode;
        var direction = TermVisuals.DirectionApplies(inForce) ? TermDirectionVisuals.Info(inForce.Direction) : null;

        return new OdsTermHistorySeries
        {
            Key = labelKey ?? "",
            Label = TermVisuals.DisplayName(inForce),
            Value = TermVisuals.FormatValue(inForce, formatMoney),
            ToneLabel = direction?.Label,
            ToneColor = direction?.Color,
            Color = direction?.Color ?? TermVisuals.Info(inForce).Ink,
            Group = pct ? "pct" : $"amt:{currency}",
            // One line is one unit and one currency. A series repriced into another currency keeps
            // its name (the series key is the label, as on the server) but only the entries
            // measured like the one in force are plotted: an axis cannot read EUR and USD at once,
            // and joining them would draw a currency change as a price move.
            Points = entries
                .Where(t => t.ValueUnit == inForce.ValueUnit
                            && (pct || string.Equals(t.CurrencyCode, currency, StringComparison.OrdinalIgnoreCase)))
                .Select(t => new OdsStepPoint(DateOnly.FromDateTime(t.EffectiveFrom), t.Value) { Id = t.TermId.ToString() })
                .ToList(),
            Format = pct
                ? v => (v < 0 ? "−" : "") + TermVisuals.PctStr(Math.Abs(v))
                : v => formatMoney(v, currency),
            AxisFormat = pct ? PercentTick : AmountTick,
        };
    }

    private static string PercentTick(decimal v) => (v < 0 ? "−" : "") + TermVisuals.PctStr(Math.Abs(v));

    // No currency code on the tick — it is stated once, in the value. Whole units at 10 and above: a
    // rent axis reading "2,438.75" is noise.
    private static string AmountTick(decimal v) =>
        (v < 0 ? "−" : "") + Math.Abs(v).ToString(Math.Abs(v) >= 10 ? "#,##0" : "#,##0.##", CultureInfo.InvariantCulture);

}
