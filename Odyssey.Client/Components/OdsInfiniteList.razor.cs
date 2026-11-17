using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace Odyssey.Client.Components;

public partial class OdsInfiniteList<TItem>
{
    /// <summary>The full (already-loaded) item set.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<TItem> Items { get; set; } = [];

    /// <summary>
    /// Renders one card. Required in the card modes; <see cref="FooterOnly"/> renders no items and
    /// leaves it unset (which is why it is not <c>EditorRequired</c>).
    /// </summary>
    [Parameter] public RenderFragment<TItem>? RenderItem { get; set; }

    /// <summary>Stable per-item key (for @key + RevealKey matching).</summary>
    [Parameter] public Func<TItem, object>? ItemKey { get; set; }

    /// <summary>Cards appended per batch. Owned by the page's "Load N at a time" control.</summary>
    [Parameter] public int BatchSize { get; set; } = 25;

    /// <summary>Plural noun for the status pill ("accounts", "budgets").</summary>
    [Parameter] public string Noun { get; set; } = "items";

    /// <summary>Rendered after the list once everything is loaded (e.g. an OdsAddRow).</summary>
    [Parameter] public RenderFragment? Trailing { get; set; }

    /// <summary>Force-reveal up to the item whose <see cref="ItemKey"/> equals this (jump-to).</summary>
    [Parameter] public object? RevealKey { get; set; }

    // ── List states (forwarded verbatim to OdsListStatus — see its docs) ──────
    /// <summary>The first fetch is in flight — renders a spinner instead of the list.</summary>
    [Parameter] public bool Loading { get; set; }

    /// <summary>A background refetch is in flight — renders the indeterminate bar above the list.</summary>
    [Parameter] public bool Refetching { get; set; }

    /// <summary>The fetch failed — renders the error state with a Retry, never the empty state.</summary>
    [Parameter] public bool Error { get; set; }

    /// <summary>Retry the failed fetch.</summary>
    [Parameter] public EventCallback OnRetry { get; set; }

    /// <summary>Optional detail line under the error title.</summary>
    [Parameter] public string? ErrorDescription { get; set; }

    /// <summary>A search or filter is narrowing the list, so empty means "no matches", not "first run".</summary>
    [Parameter] public bool HasFilters { get; set; }

    /// <summary>Clear every filter and search — offered from the filtered-empty state.</summary>
    [Parameter] public EventCallback OnClearFilters { get; set; }

    /// <summary>The filtered-empty title, when the page words it itself (see <c>OdsListStatus</c>).</summary>
    [Parameter] public string? FilteredEmptyTitle { get; set; }

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

    /// <summary>
    /// Raised after render whenever the MEMBERSHIP of the rendered window changes, compared by
    /// <see cref="ItemKey"/>: the first render, each reveal, a list whose rendered keys differ. A new
    /// <see cref="Items"/> instance with the same rendered keys — a freshly sorted array — does not raise it.
    /// For a host that fetches something per rendered item (the journal's weather, issue #363 §3.6); optional,
    /// and a host that leaves it unset renders exactly as before.
    /// </summary>
    [Parameter] public EventCallback<IReadOnlyList<TItem>> OnShownChanged { get; set; }

    // ── Remote mode (issue #473 §4.4) ────────────────────────────────────────
    /// <summary>
    /// The server pages by cursor: every item in <see cref="Items"/> renders (no windowing), and the
    /// sentinel or the pill asks the page for the next page through <see cref="LoadMore"/>. Off by
    /// default, so every in-memory page is unchanged.
    /// </summary>
    [Parameter] public bool Remote { get; set; }

    /// <summary>The server total from the first page, for the pill. <c>null</c> leaves "of N" out.</summary>
    [Parameter] public int? RemoteTotal { get; set; }

    /// <summary>The server stopped counting at <see cref="RemoteTotal"/>: the pill reads "10,000+".</summary>
    [Parameter] public bool RemoteTotalCapped { get; set; }

    /// <summary>There is a next page (<c>nextCursor != null</c>).</summary>
    [Parameter] public bool HasMoreRemote { get; set; }

    /// <summary>
    /// Asks the page for the next page. The argument is <c>true</c> when the user pressed the pill (the
    /// page then moves focus to the first new card, §6.6) and <c>false</c> for a scroll-driven load. Never
    /// called concurrently, and not on scroll after a failure until the pill's Retry is pressed.
    /// </summary>
    [Parameter] public EventCallback<bool> LoadMore { get; set; }

    /// <summary>An append is in flight (owned by the page). The pill reads "Loading more …".</summary>
    [Parameter] public bool LoadingMore { get; set; }

    /// <summary>The last append failed (owned by the page). The pill reads "Couldn't load more. Retry".</summary>
    [Parameter] public bool LoadMoreFailed { get; set; }

    /// <summary>
    /// Changes on every fresh first page. Only a change of this resets the remote list's append
    /// tracking; a growing <see cref="Items"/> under the same key is an append.
    /// </summary>
    [Parameter] public object? ResetKey { get; set; }

    // ── Footer-only mode (issue #473 §3.4, decision 4 at merge) ──────────────
    /// <summary>
    /// Renders only the sentinel and the pill — no items and no loading, error or empty states — after
    /// another component (an <c>OdsRecordTable</c>) that draws the page-held window. <see cref="Items"/>
    /// is the whole (sorted, filtered) set and gives the total; <see cref="WindowSize"/> is the window.
    /// </summary>
    [Parameter] public bool FooterOnly { get; set; }

    /// <summary>The page-held window size in <see cref="FooterOnly"/> mode. Bind it with <c>@bind-WindowSize</c>.</summary>
    [Parameter] public int WindowSize { get; set; }

    /// <summary>Raised with the grown window (<see cref="BatchSize"/> more, capped at the total).</summary>
    [Parameter] public EventCallback<int> WindowSizeChanged { get; set; }

    private bool _inFlight;
    private bool _appendPending;
    private bool _recheck;
    private bool _lastLoadingMore;
    private bool _lastLoadMoreFailed;
    private int _lastRemoteCount = -1;
    private object? _lastResetKey;
    private string _announcement = string.Empty;

    private bool RemoteHasMore => HasMoreRemote && !Loading && !Error;

    // One load at a time, and none on scroll after a failure.
    private bool RemoteCanLoad => RemoteHasMore && !LoadingMore && !LoadMoreFailed && !_inFlight;

    private string RemotePillClass => LoadMoreFailed && !LoadingMore
        ? "odc-infinite-pill odc-infinite-btn is-error"
        : "odc-infinite-pill odc-infinite-btn";

    private int FooterShown => Math.Clamp(WindowSize, 0, Total);

    private bool FooterHasMore => FooterShown < Total;

    private bool ShouldObserve => FooterOnly ? FooterHasMore : Remote ? RemoteCanLoad : HasMore;

    private HashSet<object>? _lastShownKeys;

    private readonly string _id = $"odc-inf-{Guid.NewGuid():N}";
    private ElementReference _sentinel;
    private bool _observing;
    private IJSObjectReference? _module;
    private DotNetObjectReference<OdsInfiniteList<TItem>>? _selfRef;

    private int _visible;
    private int _lastTotal = -1;
    private int _lastBatch = -1;
    private object? _lastReveal;

    private int Total => Items.Count;
    private int Shown => Math.Min(_visible, Total);
    // Only while the list itself is on screen: a loading/error state renders no sentinel, and
    // observing a default ElementReference would throw in the interop layer.
    private bool HasMore => !Loading && !Error && Shown < Total;

    protected override void OnParametersSet()
    {
        if (Remote && !FooterOnly)
        {
            TrackRemote();
            return;
        }

        // Reset the window to the first batch when the RESULT SET changes (a search / filter that
        // changes the count) or the batch size changes — keyed on the count, not the array identity
        // (a freshly sorted array changes every render and would pin the list to the first batch).
        if (Total != _lastTotal || BatchSize != _lastBatch)
        {
            _visible = BatchSize;
            _lastTotal = Total;
            _lastBatch = BatchSize;
        }

        // Grow the window to include a jumped-to item so it renders (and can scroll into view).
        if (!Equals(RevealKey, _lastReveal))
        {
            _lastReveal = RevealKey;
            if (RevealKey is not null && ItemKey is not null)
            {
                var index = IndexOfKey(RevealKey);
                if (index >= 0 && index + 1 > _visible)
                    _visible = index + 1;
            }
        }
    }

    private void TrackRemote()
    {
        var count = Items.Count;
        if (_lastRemoteCount < 0 || !Equals(ResetKey, _lastResetKey))
        {
            // A fresh first page: the new rows are not an append, and the page announces its own count.
            _lastResetKey = ResetKey;
            _lastRemoteCount = count;
            _appendPending = false;
            _announcement = string.Empty;
            _recheck = true;
        }
        else
        {
            if (LoadingMore)
            {
                _appendPending = true;
                if (!_lastLoadingMore)
                    _announcement = string.Empty;   // a retry clears the old failure, so a second one is read again
            }

            if (count > _lastRemoteCount && _appendPending)
            {
                _announcement = $"{count - _lastRemoteCount:N0} more {Noun} loaded, {count:N0} {OfTotal()}.";
                _appendPending = false;
                _recheck = true;
            }
            else if (LoadMoreFailed && !_lastLoadMoreFailed)
            {
                _announcement = $"Couldn’t load more {Noun}.";
                _appendPending = false;
            }

            _lastRemoteCount = count;
        }

        if (_lastLoadingMore && !LoadingMore)
            _recheck = true;
        _lastLoadingMore = LoadingMore;
        _lastLoadMoreFailed = LoadMoreFailed;
    }

    private string OfTotal() => RemoteTotal switch
    {
        { } total when RemoteTotalCapped => $"of more than {total:N0}",
        { } total => $"of {total:N0}",
        _ => "loaded",
    };

    private async Task OnPillPressed()
    {
        // aria-disabled while loading: a press is a no-op, and focus stays on the pill.
        if (LoadingMore || !RemoteHasMore)
            return;
        await RequestMoreAsync(fromPill: true);
    }

    private async Task RequestMoreAsync(bool fromPill)
    {
        if (_inFlight)
            return;
        _inFlight = true;
        _appendPending = true;
        try
        {
            await LoadMore.InvokeAsync(fromPill);
        }
        finally
        {
            _inFlight = false;
            _recheck = true;
        }

        // The page's own render may not reach this component (nothing changed), and the sentinel
        // still needs its re-check: a tall screen fills itself page by page (§4.4 "no stall").
        await InvokeAsync(StateHasChanged);
    }

    private int IndexOfKey(object key)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (ItemKey is not null && Equals(ItemKey(Items[i]), key))
                return i;
        }

        return -1;
    }

    // Append the next batch — shared by the sentinel auto-load and the keyboard "load more" button.
    private async Task Grow()
    {
        if (FooterOnly)
        {
            if (!FooterHasMore)
                return;
            _recheck = true;
            await WindowSizeChanged.InvokeAsync(Math.Min(FooterShown + BatchSize, Total));
            return;
        }

        if (!HasMore)
            return;

        _visible = Math.Min(_visible + BatchSize, Total);
        StateHasChanged();
    }

    [JSInvokable]
    public Task OnSentinelVisible()
    {
        if (Remote && !FooterOnly)
            return RemoteCanLoad ? RequestMoreAsync(fromPill: false) : Task.CompletedTask;
        return InvokeAsync(Grow);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _selfRef = DotNetObjectReference.Create(this);
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/infinite-scroll.js");
        }

        if (_module is not null)
        {
            // A re-check re-observes: a fresh IntersectionObserver reports the sentinel's CURRENT state,
            // so one still in view after an append asks again instead of stalling (§4.4).
            var recheck = _recheck && (Remote || FooterOnly);
            _recheck = false;
            if (ShouldObserve && (!_observing || recheck))
            {
                await _module.InvokeVoidAsync("observe", _id, _sentinel, _selfRef);
                _observing = true;
            }
            else if (!ShouldObserve && _observing)
            {
                await _module.InvokeVoidAsync("unobserve", _id);
                _observing = false;
            }
        }

        // Last, so a host callback that throws cannot leave the sentinel unobserved and the list unable to grow.
        await RaiseShownChangedAsync();
    }

    private async Task RaiseShownChangedAsync()
    {
        if (!OnShownChanged.HasDelegate || Loading || Error || FooterOnly)
            return;

        var shown = (Remote ? Items : Items.Take(Shown)).ToList();
        var keys = shown.Select(item => ItemKey?.Invoke(item) ?? item!).ToHashSet();
        if (_lastShownKeys is not null && _lastShownKeys.SetEquals(keys))
            return;

        _lastShownKeys = keys;
        await OnShownChanged.InvokeAsync(shown);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null)
            {
                if (_observing)
                    await _module.InvokeVoidAsync("unobserve", _id);
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone — nothing to clean up.
        }

        _selfRef?.Dispose();
    }
}
