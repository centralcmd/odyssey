namespace Odyssey.Client.Components;

// ─────────────────────────────────────────────────────────────────────────────
//  Sorting — the sort descriptor a page persists, the per-page sort field
//  declarations behind OdsSortSelect, and the comparison helpers that keep every
//  list ordering identical (nulls last, stable id tiebreak). Pure functions, so
//  they are covered directly by Odyssey.Client.Tests/OdsSortHelpersTests.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Controlled sort state for an <see cref="OdsTable{TRow}"/> (the column key + direction).</summary>
public sealed record OdsTableSort(string Key, OdsSortDirection Dir);

/// <summary>
/// One curated sort field for <see cref="OdsSortSelect{TRow}"/> — an entry of a page's static
/// sort-key allowlist (§6 of the per-page sorting spec). <see cref="Type"/> drives the natural
/// default direction and the typed direction label; <see cref="DefaultDir"/> overrides that
/// natural default. <see cref="SortValue"/> is the row projection the hand-rolled
/// <see cref="OdsSortHelpers.SortRows{TRow}"/> orders by — unused on <c>OdsRecordTable</c>-backed
/// pages, which sort via the table's own column <c>SortValue</c>.
/// </summary>
public sealed record OdsSortField<TRow>
{
    /// <summary>Sort key — matches an <see cref="OdsTableSort.Key"/> (and a sortable column key on table pages).</summary>
    public required string Key { get; init; }

    /// <summary>Visible label in the "Sort by" dropdown.</summary>
    public required string Label { get; init; }

    /// <summary>Field data type — drives the default direction and the typed label.</summary>
    public OdsSortType Type { get; init; } = OdsSortType.Text;

    /// <summary>Override the type's natural default direction.</summary>
    public OdsSortDirection? DefaultDir { get; init; }

    /// <summary>Comparable projection used by <see cref="OdsSortHelpers.SortRows{TRow}"/> (hand-rolled pages).</summary>
    public Func<TRow, IComparable?>? SortValue { get; init; }
}

/// <summary>
/// The shared sorting authority for the per-page sorting feature (§8.4/§8.5): natural default
/// directions, typed direction labels, and the stable client-side ordering hand-rolled list pages
/// use. Consulted identically by <see cref="OdsSortSelect{TRow}"/> field changes and by the
/// controlled <see cref="OdsRecordTable{TRow}"/> header-click column changes, so the two can never
/// diverge.
/// </summary>
public static class OdsSortHelpers
{
    /// <summary>The natural default direction for a field type — Number/Date → Desc, Text/Status → Asc.</summary>
    public static OdsSortDirection DefaultDir(OdsSortType type) => type switch
    {
        OdsSortType.Number or OdsSortType.Date => OdsSortDirection.Desc,
        _ => OdsSortDirection.Asc,
    };

    /// <summary>The default direction for a field — its explicit <see cref="OdsSortField{TRow}.DefaultDir"/>, else the type's.</summary>
    public static OdsSortDirection DefaultDir<TRow>(OdsSortField<TRow> field) =>
        field.DefaultDir ?? DefaultDir(field.Type);

    /// <summary>
    /// The direction to adopt when an <see cref="OdsRecordTable{TRow}"/> header click switches to a
    /// different <paramref name="column"/> (§8.4). When <paramref name="keepDir"/> is set and there is
    /// a <paramref name="current"/> sort, the current direction is preserved; otherwise the direction
    /// comes from the column's <see cref="OdsRecordColumn{TRow}.SortDefaultDir"/>, else its
    /// <see cref="OdsRecordColumn{TRow}.SortType"/> natural default. A null column (no match for the
    /// clicked key) falls back to <see cref="OdsSortDirection.Asc"/>. Same-column toggles never reach
    /// here — the table flips the direction directly.
    /// </summary>
    public static OdsSortDirection ColumnChangeDir<TRow>(
        OdsRecordColumn<TRow>? column, OdsTableSort? current, bool keepDir)
    {
        if (keepDir && current is not null)
            return current.Dir;

        return column is null
            ? OdsSortDirection.Asc
            : column.SortDefaultDir ?? DefaultDir(column.SortType);
    }

    /// <summary>The typed, user-facing direction label per §4.4 (e.g. <c>DirLabel(Date, Desc)</c> → "Newest first").</summary>
    public static string DirLabel(OdsSortType type, OdsSortDirection dir) => (type, dir) switch
    {
        (OdsSortType.Text, OdsSortDirection.Asc) => "A → Z",
        (OdsSortType.Text, OdsSortDirection.Desc) => "Z → A",
        (OdsSortType.Number, OdsSortDirection.Asc) => "Low → High",
        (OdsSortType.Number, OdsSortDirection.Desc) => "High → Low",
        (OdsSortType.Date, OdsSortDirection.Asc) => "Oldest first",
        (OdsSortType.Date, OdsSortDirection.Desc) => "Newest first",
        (OdsSortType.Status, OdsSortDirection.Asc) => "Defined order",
        (OdsSortType.Status, OdsSortDirection.Desc) => "Reversed",
        _ => dir.ToString(),
    };

    /// <summary>
    /// Resolve a persisted <paramref name="key"/> + optional <paramref name="dir"/> into a complete
    /// <see cref="OdsTableSort"/>, validated against the page's field allowlist (§11): an unknown key
    /// falls back to <paramref name="fallback"/>; a null (or out-of-range) direction resolves via the
    /// field's default.
    /// </summary>
    public static OdsTableSort Resolve<TRow>(
        IReadOnlyList<OdsSortField<TRow>> fields, string? key, OdsSortDirection? dir, OdsTableSort fallback)
    {
        if (string.IsNullOrEmpty(key)) return fallback;
        var field = fields.FirstOrDefault(f => f.Key == key);
        if (field is null) return fallback;
        var direction = dir is { } d && Enum.IsDefined(d) ? d : DefaultDir(field);
        return new OdsTableSort(field.Key, direction);
    }

    /// <summary>
    /// Stable client-side ordering for hand-rolled list pages, from the same <paramref name="fields"/>
    /// list that feeds the dropdown. Applies AFTER search + filters; null/empty keys sort LAST in both
    /// directions; ties resolve on the record <paramref name="getId"/>, then input order.
    /// </summary>
    public static IReadOnlyList<TRow> SortRows<TRow>(
        IEnumerable<TRow> rows,
        IReadOnlyList<OdsSortField<TRow>> fields,
        OdsTableSort? sort,
        Func<TRow, string> getId)
    {
        var list = rows as IReadOnlyList<TRow> ?? rows.ToList();
        if (sort?.Key is not { } key) return list;
        var field = fields.FirstOrDefault(f => f.Key == key);
        if (field?.SortValue is not { } sortValue) return list;

        var mul = sort.Dir == OdsSortDirection.Asc ? 1 : -1;
        return [.. list
            .Select((row, index) => (row, index))
            .OrderBy(x => x, Comparer<(TRow row, int index)>.Create((a, b) =>
            {
                var va = sortValue(a.row);
                var vb = sortValue(b.row);
                var ea = IsEmpty(va);
                var eb = IsEmpty(vb);
                if (ea || eb)
                    return ea && eb ? Tie(a, b) : (ea ? 1 : -1);   // empties last, both directions
                var cmp = mul * CompareValues(va, vb);
                return cmp != 0 ? cmp : Tie(a, b);
            }))
            .Select(x => x.row)];

        int Tie((TRow row, int index) a, (TRow row, int index) b)
        {
            var cmp = string.CompareOrdinal(getId(a.row), getId(b.row));
            return cmp != 0 ? cmp : a.index - b.index;
        }
    }

    private static bool IsEmpty(IComparable? value) =>
        value is null || (value is string s && s.Length == 0);

    private static int CompareValues(IComparable? a, IComparable? b)
    {
        if (a is null) return b is null ? 0 : -1;
        if (b is null) return 1;
        return a.CompareTo(b);
    }
}
