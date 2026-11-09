using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace Odyssey.Client.Components;

public partial class OdsRecordTable<TRow>
{
    [Inject] private IJSRuntime Js { get; set; } = default!;

    /// <summary>Rows to render (already filtered by the parent).</summary>
    [Parameter, EditorRequired] public IEnumerable<TRow> Rows { get; set; } = [];

    /// <summary>Column definitions, left → right (excluding the leading + actions cells).</summary>
    [Parameter, EditorRequired] public IReadOnlyList<OdsRecordColumn<TRow>> Columns { get; set; } = [];

    /// <summary>Stable row identity. Defaults to the row instance itself.</summary>
    [Parameter] public Func<TRow, object> RowKey { get; set; } = static r => r!;

    /// <summary>
    /// Optional stable id for a row's menu trigger, keyed on the row key. By default the id is a per-table slot,
    /// stable only for this instance; a host that returns focus to the trigger from a dialog across a re-render
    /// supplies one derived from the record (centralcmd/odyssey#445).
    /// </summary>
    [Parameter] public Func<object, string>? MenuTriggerIdFor { get; set; }

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

    /// <summary>Read-only panel shown when a row is expanded. Omit — together with
    /// <see cref="RenderEdit"/> — for a flat table whose columns already show every field.</summary>
    [Parameter] public RenderFragment<TRow>? RenderDetail { get; set; }

    /// <summary>Edit panel shown when a row is in edit mode. Omit for read-only tables.</summary>
    [Parameter] public Func<TRow, OdsRecordEditContext, RenderFragment>? RenderEdit { get; set; }

    /// <summary>
    /// Persist a row edit — raised with the row key and the edit patch. Call
    /// <see cref="OdsRecordSaveEventArgs.Fail"/> when the write did not persist: the row then stays in
    /// edit mode and no "Saved" flash is shown.
    /// </summary>
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

    /// <summary>
    /// Names the row for its expand toggle (Odyssey Design System · RecordTable <c>rowLabel</c>). When given, the
    /// toggle reads "Show details for {label}" / "Hide details for {label}" instead of "Expand row" /
    /// "Collapse row", and its <c>aria-controls</c> points at a <c>role="group"</c> labelled
    /// "Details for {label}" inside the detail panel. Use it wherever several rows would otherwise share one
    /// toggle name. Omit it and the toggle and panel are exactly as before.
    /// </summary>
    [Parameter] public Func<TRow, string>? RowLabel { get; set; }

    /// <summary>
    /// The table's accessible name from its own content, rendered as a <c>&lt;caption&gt;</c> (Odyssey Design
    /// System · RecordTable <c>caption</c>, issue #451). Prefer it over <see cref="AriaLabel"/>.
    /// </summary>
    [Parameter] public string? Caption { get; set; }

    /// <summary>Keep the <see cref="Caption"/> visually hidden (still read by assistive technology).</summary>
    [Parameter] public bool CaptionHidden { get; set; }

    /// <summary>
    /// Key of the ONE column whose body cells render as <c>&lt;th scope="row"&gt;</c>, styled like a normal cell
    /// (issue #451) — the row's identity, announced as a screen reader moves across the other cells.
    /// </summary>
    [Parameter] public string? RowHeader { get; set; }

    /// <summary>
    /// Marks a row whose save is in flight (Odyssey Design System · RecordTable <c>rowBusy</c>, issue #451).
    /// While true the actions cell shows a small spinner named "Saving {RowLabel}" instead of the menu — never
    /// an empty or disabled one — and the chevron stays. Focus on that row's menu moves to the row header
    /// cell (the <see cref="RowHeader"/> column, else the first) and back to the menu trigger once idle.
    /// Pass <see cref="RowLabel"/> with it so the spinner names its record.
    /// </summary>
    [Parameter] public Func<TRow, bool>? RowBusy { get; set; }

    // Rows rendered busy. Updated only after the focus check below, so the render that drops a row's menu
    // happens after the check has read where focus was.
    private HashSet<object> _busyKeys = [];
    private readonly HashSet<object> _focusToHead = [];
    private readonly HashSet<object> _movedToHead = [];
    private readonly HashSet<object> _focusBackToMenu = [];

    private OdsTableSort? _sort;
    private readonly HashSet<object> _openIds = [];

    // The chevron's aria-controls target (Odyssey Design System · RecordTable): the detail row's id,
    // unique per table instance so two tables on one page never share one.
    private readonly string _detailIdPrefix = $"odc-rec-detail-{Guid.NewGuid():N}";
    private readonly HashSet<object> _editIds = [];
    private readonly HashSet<object> _savedIds = [];

    // Stable per-row element ids for focus management — keyed by the row key, not its index, so a
    // sort between a request and the render that honours it still finds the right row.
    private readonly string _tableId = $"odc-rec-{Guid.NewGuid():N}";
    private readonly Dictionary<object, int> _rowSlots = [];
    private int _nextSlot;

    private string SlotFor(object key)
    {
        if (!_rowSlots.TryGetValue(key, out var slot))
            _rowSlots[key] = slot = _nextSlot++;
        return $"{_tableId}-{slot}";
    }

    // Slots of rows no longer passed in are dropped, so a paged table's map tracks the page rather than
    // every row it has ever shown. A row that returns gets a fresh slot; ids are never reused.
    protected override void OnParametersSet()
    {
        if (_rowSlots.Count == 0)
            return;
        var present = Rows.Select(RowKey).ToHashSet();
        foreach (var gone in _rowSlots.Keys.Where(k => !present.Contains(k)).ToList())
            _rowSlots.Remove(gone);
    }

    protected override async Task OnParametersSetAsync()
    {
        var next = RowBusy is null ? [] : Rows.Where(RowBusy).Select(RowKey).ToHashSet();
        if (next.SetEquals(_busyKeys))
            return;
        foreach (var key in next.Where(k => !_busyKeys.Contains(k)))
        {
            // Read where focus sits while the row's menu is still in the DOM.
            try
            {
                if (await Js.InvokeAsync<bool>("odsRecFocusInMenu", MenuIdFor(key)))
                    _focusToHead.Add(key);
            }
            catch (JSException) { }
        }
        foreach (var key in _busyKeys.Where(k => !next.Contains(k)))
            if (_movedToHead.Remove(key))
                _focusBackToMenu.Add(key);
        _busyKeys = next;
    }

    private string HeadIdFor(object key) => $"{SlotFor(key)}-head";

    /// <summary>
    /// Moves focus to a row's header cell (the <see cref="RowHeader"/> column, else the first) — the landing
    /// spot a page picks after removing a row ("next row's header, else the previous one", issue #452 §3.3).
    /// Returns false when the row is not rendered, so the caller can fall back (the page <c>h1</c>).
    /// </summary>
    public async Task<bool> FocusRowHeaderAsync(object key)
    {
        if (!_rowSlots.ContainsKey(key))
            return false;
        try
        { return await Js.InvokeAsync<bool>("odsRecFocusCell", HeadIdFor(key)); }
        catch (JSException) { return false; }
    }

    // The row's menu items, or null when none is available — a read-only row renders no trigger at all.
    private IReadOnlyList<OdsMenuItem>? MenuItemsFor(TRow row, object key, bool expanded, bool editing)
    {
        var items = Actions!(row, ActionCtx(key, expanded, editing));
        return items.Any(i => !i.Disabled && !i.Divider && string.IsNullOrEmpty(i.Header)) ? items : null;
    }

    private string MenuIdFor(object key) => MenuTriggerIdFor?.Invoke(key) ?? $"{SlotFor(key)}-menu";

    private string ExpandIdFor(object key) => $"{SlotFor(key)}-expand";

    private string PanelIdFor(object key) => $"{SlotFor(key)}-panel";

    private enum FocusMove { None, IntoPanel, EditField, Chevron, MenuTrigger }

    private (FocusMove Move, object? Key) _pendingFocus;

    private bool _clicksTracked;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_clicksTracked)
        {
            _clicksTracked = true;
            try
            { await Js.InvokeVoidAsync("odsRecTrackClicks", _tableId); }
            catch (JSException) { }
        }

        await MoveBusyFocusAsync();

        var (move, key) = _pendingFocus;
        if (move == FocusMove.None || key is null)
            return;
        _pendingFocus = (FocusMove.None, null);
        try
        {
            switch (move)
            {
                // Two frames for every move a menu item can start (View details, Edit, Collapse): the row
                // menu restores focus to its own trigger as it closes, and the move has to land after
                // that restore, never race it.
                case FocusMove.IntoPanel:
                case FocusMove.EditField:
                    await Js.InvokeVoidAsync("odsFocusFirstIn", PanelIdFor(key), true, 2);
                    break;
                case FocusMove.Chevron:
                    await Js.InvokeVoidAsync("odsFocusFirstIn", ExpandIdFor(key), true, 2);
                    break;
                case FocusMove.MenuTrigger:
                    await Js.InvokeVoidAsync("odsFocusFirstIn", MenuIdFor(key), false, 0);
                    break;
            }
        }
        catch (JSException) { }
    }

    private async Task MoveBusyFocusAsync()
    {
        if (_focusToHead.Count == 0 && _focusBackToMenu.Count == 0)
            return;
        var toHead = _focusToHead.ToList();
        var toMenu = _focusBackToMenu.ToList();
        _focusToHead.Clear();
        _focusBackToMenu.Clear();
        try
        {
            foreach (var key in toHead)
            {
                await Js.InvokeVoidAsync("odsRecFocusEl", HeadIdFor(key));
                _movedToHead.Add(key);
            }
            // Back to the trigger only if focus is still where the busy state put it.
            foreach (var key in toMenu)
                await Js.InvokeVoidAsync("odsRecFocusMenuFromHead", HeadIdFor(key), MenuIdFor(key));
        }
        catch (JSException) { }
    }

    protected override void OnInitialized() => _sort = DefaultSort;

    // Controlled when the parent bound Sort (@bind-Sort → SortChanged has a delegate). Then the bound
    // value drives everything and the internal seed is superseded; unbound keeps the legacy state.
    private bool Controlled => SortChanged.HasDelegate;

    private OdsTableSort? EffectiveSort => Controlled ? (Sort ?? DefaultSort) : _sort;

    private int ColSpan => (Leading is not null ? 1 : 0) + Columns.Count + 1;

    /// <summary>
    /// Whether rows have anywhere to expand to. With neither panel supplied the table drops the
    /// chevron, the row-click toggle and the pointer cursor rather than opening an empty drawer —
    /// the design system's flat record table (Currencies, Exchange rates, tag admin, Files).
    /// </summary>
    private bool CanExpand => RenderDetail is not null || RenderEdit is not null;

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

    private static string ExpandLabel(bool expanded, string? label) => label is null
        ? expanded ? "Collapse row" : "Expand row"
        : $"{(expanded ? "Hide" : "Show")} details for {label}";

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
    private async Task OnRowClickAsync(object id, bool editing)
    {
        if (editing || !CanExpand)
            return;
        // A click on a control inside a cell (a switch, a link) belongs to that control.
        try
        {
            if (await Js.InvokeAsync<bool>("odsRecClickWasInteractive", _tableId))
                return;
        }
        catch (JSException) { }
        ToggleRow(id, fromMenu: false);
    }

    // Focus moves only for the row menu's View details / Collapse, whose item unmounts with its popover.
    // The chevron and a row click keep focus where it is, so the aria-expanded control that was just
    // activated can be pressed again to undo it (WCAG 2.4.3, 3.2.2).
    private void ToggleRow(object id, bool fromMenu)
    {
        if (!CanExpand)
            return;
        if (_openIds.Contains(id))
        {
            _openIds.Remove(id);
            if (fromMenu)
                _pendingFocus = (FocusMove.Chevron, id);
        }
        else
        {
            OpenRow(id);
            if (fromMenu)
                _pendingFocus = (FocusMove.IntoPanel, id);
        }
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
        _pendingFocus = (FocusMove.EditField, id);
    }

    private void EndEdit(object id)
    {
        if (_editIds.Remove(id))
            _pendingFocus = (FocusMove.MenuTrigger, id);
    }

    // Leaves edit mode and flashes "Saved" only when the host did not report a failure (issue #252).
    // A throwing OnSave is routed to the renderer's error handling rather than escaping into a task
    // nobody observes, and never reaches the flash. The returned task completes once the outcome is
    // applied; the flash runs on detached so an awaiting template is not held for the confirm window.
    private async Task DoSaveAsync(object id, object? patch)
    {
        var args = new OdsRecordSaveEventArgs(id, patch);
        try
        {
            if (OnSave.HasDelegate)
                await OnSave.InvokeAsync(args);
        }
        catch (Exception ex)
        {
            await DispatchExceptionAsync(ex);
            return;
        }

        if (args.Failed)
        {
            StateHasChanged();
            return;
        }

        EndEdit(id);
        _ = FlashSavedAsync(id);
    }

    private async Task FlashSavedAsync(object id)
    {
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
        Toggle = () => ToggleRow(id, fromMenu: true),
        StartEdit = () => StartEdit(id),
        Remove = () => DoDelete(id),
        MenuTriggerId = MenuIdFor(id),
    };

    private OdsRecordEditContext EditCtx(object id) => new()
    {
        Save = patch => DoSaveAsync(id, patch),
        Cancel = () => EndEdit(id),
        Done = () => { EndEdit(id); StateHasChanged(); },
    };
}
