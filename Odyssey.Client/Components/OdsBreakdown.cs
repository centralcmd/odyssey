namespace Odyssey.Client.Components;

/// <summary>
/// Helpers that build <see cref="OdsBreakdownRow"/> lists for the page-Overview summary grids
/// (Odyssey Design System · Components.jsx <c>odcTypeRows</c> / <c>odcStatusRows</c>). "By type"
/// keeps only the types present (count &gt; 0) in registry order; "By status" shows every defined
/// status including zero counts — matching the Contracts / Insurance breakdowns.
/// </summary>
public static class OdsBreakdown
{
    /// <summary>Finance tone → CSS colour, mirroring the design system's ODC_TONE map.</summary>
    public static string Tone(string tone) => tone switch
    {
        "income" => "var(--finance-income)",
        "expense" => "var(--finance-expense)",
        "pending" => "var(--finance-pending)",
        "info" => "var(--sea-400)",
        "tag" => "var(--tag-text)",
        _ => "var(--mud-palette-text-secondary)", // outline / neutral
    };

    /// <summary>"By type" rows: count <paramref name="items"/> by key, keep only present types
    /// (count &gt; 0) in <paramref name="order"/>, drawing icon/colour/label from <paramref name="visual"/>.</summary>
    public static IReadOnlyList<OdsBreakdownRow> TypeRows<T, TKey>(
        IEnumerable<T> items, Func<T, TKey> typeOf, IReadOnlyList<TKey> order,
        Func<TKey, (string Icon, string Color, string Label)> visual) where TKey : notnull
    {
        var counts = Count(items, typeOf);
        return CountedTypeRows(counts, order, visual);
    }

    /// <summary>"By status" rows: count <paramref name="items"/> by key, show ALL <paramref name="defs"/>
    /// (including zero counts) in order.</summary>
    public static IReadOnlyList<OdsBreakdownRow> StatusRows<T, TKey>(
        IEnumerable<T> items, Func<T, TKey> statusOf, params OdsBreakdownDef<TKey>[] defs) where TKey : notnull
    {
        var counts = Count(items, statusOf);
        return CountedStatusRows(k => counts.GetValueOrDefault(k), defs);
    }

    /// <summary>
    /// "By type" rows from counts a summary endpoint already computed (issue #372) — same output as
    /// <see cref="TypeRows{T, TKey}"/>, without needing the items themselves in the browser.
    /// </summary>
    public static IReadOnlyList<OdsBreakdownRow> CountedTypeRows<TKey>(
        IReadOnlyDictionary<TKey, int> counts, IReadOnlyList<TKey> order,
        Func<TKey, (string Icon, string Color, string Label)> visual) where TKey : notnull =>
        order
            .Where(k => counts.GetValueOrDefault(k) > 0)
            .Select(k =>
            {
                var (icon, color, label) = visual(k);
                return new OdsBreakdownRow { Key = k, Icon = icon, IconColor = color, Label = label, Count = counts[k] };
            })
            .ToList();

    /// <summary>
    /// "By status" rows from counts a summary endpoint already computed (issue #372) — same output as
    /// <see cref="StatusRows{T, TKey}"/>, without needing the items themselves in the browser.
    /// </summary>
    public static IReadOnlyList<OdsBreakdownRow> CountedStatusRows<TKey>(
        Func<TKey, int> countOf, params OdsBreakdownDef<TKey>[] defs) where TKey : notnull =>
        defs
            .Select(d => new OdsBreakdownRow
            {
                Key = d.Key,
                Icon = d.Icon,
                IconColor = Tone(d.Tone),
                Label = d.Label,
                Count = countOf(d.Key),
            })
            .ToList();

    private static Dictionary<TKey, int> Count<T, TKey>(IEnumerable<T> items, Func<T, TKey> keyOf) where TKey : notnull
    {
        var counts = new Dictionary<TKey, int>();
        foreach (var x in items)
        {
            var k = keyOf(x);
            counts[k] = counts.GetValueOrDefault(k) + 1;
        }
        return counts;
    }
}

/// <summary>A fixed status definition for <see cref="OdsBreakdown.StatusRows"/> — key + label + tone + icon.</summary>
public sealed record OdsBreakdownDef<TKey>(TKey Key, string Label, string Tone, string Icon);
