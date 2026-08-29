using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace Odyssey.Client.Components;

public partial class OdsInfiniteList<TItem>
{
    /// <summary>The full (already-loaded) item set.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<TItem> Items { get; set; } = [];

    /// <summary>Renders one card.</summary>
    [Parameter, EditorRequired] public RenderFragment<TItem> RenderItem { get; set; } = default!;

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
    private void Grow()
    {
        if (!HasMore)
            return;

        _visible = Math.Min(_visible + BatchSize, Total);
        StateHasChanged();
    }

    [JSInvokable]
    public Task OnSentinelVisible()
    {
        Grow();
        return Task.CompletedTask;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _selfRef = DotNetObjectReference.Create(this);
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/infinite-scroll.js");
        }

        if (_module is null)
            return;

        if (HasMore && !_observing)
        {
            await _module.InvokeVoidAsync("observe", _id, _sentinel, _selfRef);
            _observing = true;
        }
        else if (!HasMore && _observing)
        {
            await _module.InvokeVoidAsync("unobserve", _id);
            _observing = false;
        }
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
