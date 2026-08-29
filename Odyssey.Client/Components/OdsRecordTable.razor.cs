using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Odyssey.Client.Components;

public partial class OdsRecordTable<TRow>
{
    /// <summary>Rows to render (already filtered by the parent).</summary>
    [Parameter, EditorRequired] public IEnumerable<TRow> Rows { get; set; } = [];

    /// <summary>Column definitions, left → right (excluding the leading + actions cells).</summary>
    [Parameter, EditorRequired] public IReadOnlyList<OdsRecordColumn<TRow>> Columns { get; set; } = [];

    /// <summary>Stable row identity. Defaults to the row instance itself.</summary>
    [Parameter] public Func<TRow, object> RowKey { get; set; } = static r => r!;

    /// <summary>Optional leading 36px cell (typically an OdsAvatar).</summary>
    [Parameter] public RenderFragment<TRow>? Leading { get; set; }

    /// <summary>Initial sort (uncontrolled seed). Omit for an unsorted table.</summary>
    [Parameter] public OdsTableSort? DefaultSort { get; set; }

    /// <summary>
    /// Controlled sort (§8.2). When <see cref="SortChanged"/> is bound (<c>@bind-Sort</c>) the parent
    /// owns the sort state — the internal <see cref="DefaultSort"/> seed is superseded, header clicks
    /// raise a complete <see cref="OdsTableSort"/>, and a toolbar <c>OdsSortSelect</c> and the headers
    /// stay in sync off this one value. Unbound keeps the legacy uncontrolled behaviour.
    /// </summary>
    [Parameter] public OdsTableSort? Sort { get; set; }

    /// <summary>Raised with the complete next <see cref="OdsTableSort"/> when the sort changes (enables <c>@bind-Sort</c>).</summary>
    [Parameter] public EventCallback<OdsTableSort> SortChanged { get; set; }

    /// <summary>
    /// Server-sort pass-through (issue #277 §5.1). When <c>true</c>, the server has already ordered the
    /// rows: <see cref="Sorted"/> returns <see cref="Rows"/> verbatim (each column's <c>SortValue</c> is
    /// ignored, no internal <c>OrderBy</c> runs), while the headers still render <c>aria-sort</c> from the
    /// bound <see cref="Sort"/> and a header click still raises <see cref="SortChanged"/> to the parent
    /// (which triggers a server refetch, not an in-browser reorder). Default <c>false</c> (backward-compatible).
    /// </summary>
    [Parameter] public bool ServerSort { get; set; }

    /// <summary>Keep multiple rows expanded at once (default: accordion — one at a time).</summary>
    [Parameter] public bool MultiOpen { get; set; }

    /// <summary>On clicking a new column, keep the current direction instead of resetting to asc.</summary>
    [Parameter] public bool KeepDirOnColumnChange { get; set; }

    /// <summary>Stable secondary comparison applied when the primary sort ties.</summary>
    [Parameter] public Comparison<TRow>? Tiebreak { get; set; }

    /// <summary>Build the row's overflow-menu items (rendered as an OdsMenu kebab).</summary>
    [Parameter] public Func<TRow, OdsRecordActionContext, IReadOnlyList<OdsMenuItem>>? Actions { get; set; }

    /// <summary>Read-only panel shown when a row is expanded.</summary>
    [Parameter] public RenderFragment<TRow>? RenderDetail { get; set; }

    /// <summary>Edit panel shown when a row is in edit mode. Omit for read-only tables.</summary>
    [Parameter] public Func<TRow, OdsRecordEditContext, RenderFragment>? RenderEdit { get; set; }

    /// <summary>Persist a row edit — raised with the row key and the edit patch.</summary>
    [Parameter] public EventCallback<OdsRecordSaveEventArgs> OnSave { get; set; }

    /// <summary>Remove a row — raised with the row key.</summary>
    [Parameter] public EventCallback<object> OnDelete { get; set; }

    /// <summary>How long the "Saved" flash stays up (ms). Defaults to <see cref="OdsTiming.ConfirmFlashMs"/>.</summary>
    [Parameter] public int SavedFlashMs { get; set; } = OdsTiming.ConfirmFlashMs;

    // ── List states (forwarded verbatim to OdsListStatus — see its docs) ─────
    /// <summary>The first fetch is in flight — the status cell renders a spinner and no rows.</summary>
    [Parameter] public bool Loading { get; set; }

    /// <summary>A background refetch is in flight — renders the indeterminate bar above the table.</summary>
    [Parameter] public bool Refetching { get; set; }

    /// <summary>The fetch failed — renders the error state with a Retry, never the empty state.</summary>
    [Parameter] public bool Error { get; set; }

    /// <summary>Retry the failed fetch.</summary>
    [Parameter] public EventCallback OnRetry { get; set; }

    /// <summary>Optional detail line under the error title.</summary>
    [Parameter] public string? ErrorDescription { get; set; }

    /// <summary>A search or filter is narrowing the rows, so empty means "no matches", not "first run".</summary>
    [Parameter] public bool HasFilters { get; set; }

    /// <summary>Clear every filter and search — offered from the filtered-empty state.</summary>
    [Parameter] public EventCallback OnClearFilters { get; set; }

    /// <summary>Lower-case plural noun for the state copy — "currencies", "contacts".</summary>
    [Parameter] public string Noun { get; set; } = "items";

    /// <summary>Material Icons ligature for the first-run empty state.</summary>
    [Parameter] public string EmptyIcon { get; set; } = "inbox";

    /// <summary>First-run empty title. Defaults to "No {Noun} yet".</summary>
    [Parameter] public string? EmptyTitle { get; set; }

    /// <summary>First-run empty supporting line.</summary>
    [Parameter] public string? EmptyDescription { get; set; }

    /// <summary>The first-run CTA — pass an OdsButton.</summary>
    [Parameter] public RenderFragment? CreateAction { get; set; }

    /// <summary>Replaces the whole first-run empty state, for onboarding copy that needs markup.</summary>
    [Parameter] public RenderFragment? Empty { get; set; }

    /// <summary>Utility classes for the refetch bar.</summary>
    [Parameter] public string? BarClass { get; set; }

    [Parameter] public string? Class { get; set; }

    /// <summary>Accessible name for the table — required when there is no visible caption,
    /// so assistive tech can announce what the table contains.</summary>
    [Parameter] public string? AriaLabel { get; set; }

    private OdsTableSort? _sort;
    private readonly HashSet<object> _openIds = [];
    private readonly HashSet<object> _editIds = [];
    private readonly HashSet<object> _savedIds = [];

    protected override void OnInitialized() => _sort = DefaultSort;

    // Controlled when the parent bound Sort (@bind-Sort → SortChanged has a delegate). Then the bound
    // value drives everything and the internal seed is superseded; unbound keeps the legacy state.
    private bool Controlled => SortChanged.HasDelegate;

    private OdsTableSort? EffectiveSort => Controlled ? (Sort ?? DefaultSort) : _sort;

    private int ColSpan => (Leading is not null ? 1 : 0) + Columns.Count + 1;

    /// <summary>
    /// How many placeholder rows the first fetch shows. Enough to read as a table rather than a
    /// single stray row, without implying a page size the real result may not fill.
    /// </summary>
    private const int SkeletonRowCount = 5;

    /// <summary>
    /// Per-cell alignment for the skeleton rows, so a numeric column's placeholder is right-aligned
    /// like the real cell and the layout does not jump when rows land. Mirrors the cell order in the
    /// body: the optional leading cell, then Columns, then the trailing actions cell.
    /// </summary>
    private IReadOnlyList<OdsAlign> SkeletonAlign =>
    [
        .. Leading is not null ? new[] { OdsAlign.Start } : [],
        .. Columns.Select(c => c.Align),
        OdsAlign.End,
    ];

    /// <summary>The rows actually rendered — none while loading or after a failed fetch.</summary>
    private IEnumerable<TRow> VisibleRows => Loading || Error ? [] : Sorted;

    private RenderFragment HeaderFor(OdsRecordColumn<TRow> col) =>
        col.Header ?? (b => b.AddContent(0, col.HeaderText));

    private string? RowClass(bool expanded, bool editing)
    {
        var parts = new List<string>(2);
        if (expanded)
            parts.Add("expanded");
        if (editing)
            parts.Add("editing");
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private string? CellClass(OdsRecordColumn<TRow> col)
    {
        var parts = new List<string>(2);
        if (col.Align == OdsAlign.End)
            parts.Add("odc-rec-numeric");
        if (!string.IsNullOrEmpty(col.CellClass))
            parts.Add(col.CellClass!);
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    // ── Sorting ──────────────────────────────────────────────────────────────
    // OrderBy is stable, so equal rows keep input order; an index fallback keeps
    // that guarantee when a Tiebreak is supplied. SortValue is required on
    // sortable columns — a column without one falls through to the input order.
    private IEnumerable<TRow> Sorted
    {
        get
        {
            // Server already ordered the rows — render them verbatim (headers still show aria-sort).
            if (ServerSort)
                return Rows;
            if (EffectiveSort?.Key is not { } key)
                return Rows;
            var col = Columns.FirstOrDefault(c => c.Key == key);
            if (col?.SortValue is not { } sortValue)
                return Rows;

            var mul = EffectiveSort.Dir == OdsSortDirection.Asc ? 1 : -1;
            return Rows.Select((row, index) => (row, index))
                .OrderBy(x => x, Comparer<(TRow row, int index)>.Create((a, b) =>
                {
                    var cmp = mul * CompareValues(sortValue(a.row), sortValue(b.row));
                    if (cmp != 0)
                        return cmp;
                    if (Tiebreak is not null && (cmp = Tiebreak(a.row, b.row)) != 0)
                        return cmp;
                    return a.index - b.index;
                }))
                .Select(x => x.row);
        }
    }

    private static int CompareValues(IComparable? a, IComparable? b)
    {
        if (a is null)
            return b is null ? 0 : -1;
        if (b is null)
            return 1;
        return a.CompareTo(b);
    }

    private void ToggleSort(string key)
    {
        // Sorting collapses every open row EXCEPT those mid-edit.
        _openIds.RemoveWhere(id => !_editIds.Contains(id));

        var current = EffectiveSort;
        // Toggle direction on the active column; on a column CHANGE the new direction comes from the
        // shared default-direction rule for the column's SortType (§8.4), not an unconditional Asc —
        // unless KeepDirOnColumnChange asks to preserve the current direction.
        var next = current is not null && current.Key == key
            ? current with { Dir = current.Dir == OdsSortDirection.Asc ? OdsSortDirection.Desc : OdsSortDirection.Asc }
            : new OdsTableSort(key, OdsSortHelpers.ColumnChangeDir(
                Columns.FirstOrDefault(c => c.Key == key), current, KeepDirOnColumnChange));

        if (Controlled)
            _ = SortChanged.InvokeAsync(next);
        else
            _sort = next;
    }

    // ── Open / edit state ─────────────────────────────────────────────────────
    private void OnRowClick(object id, bool editing)
    {
        if (!editing)
            ToggleRow(id);
    }

    private void ToggleRow(object id)
    {
        if (_openIds.Contains(id))
            _openIds.Remove(id);
        else
            OpenRow(id);
    }

    private void OpenRow(object id)
    {
        // Accordion: collapse other rows that aren't mid-edit.
        if (!MultiOpen)
            _openIds.RemoveWhere(x => !_editIds.Contains(x));
        _openIds.Add(id);
    }

    private void StartEdit(object id)
    {
        OpenRow(id);
        _editIds.Add(id);
    }

    private void EndEdit(object id) => _editIds.Remove(id);

    private async Task DoSaveAsync(object id, object? patch)
    {
        if (OnSave.HasDelegate)
            await OnSave.InvokeAsync(new OdsRecordSaveEventArgs(id, patch));
        EndEdit(id);
        _savedIds.Add(id);
        StateHasChanged();
        await Task.Delay(SavedFlashMs);
        _savedIds.Remove(id);
        StateHasChanged();
    }

    private void DoDelete(object id)
    {
        EndEdit(id);
        _openIds.Remove(id);
        _ = OnDelete.InvokeAsync(id);
    }

    private OdsRecordActionContext ActionCtx(object id, bool expanded, bool editing) => new()
    {
        Expanded = expanded,
        Editing = editing,
        Toggle = () => ToggleRow(id),
        StartEdit = () => StartEdit(id),
        Remove = () => DoDelete(id),
    };

    private OdsRecordEditContext EditCtx(object id) => new()
    {
        Save = patch => _ = DoSaveAsync(id, patch),
        Cancel = () => EndEdit(id),
    };
}
