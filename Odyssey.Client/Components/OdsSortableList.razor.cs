using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Odyssey.Client.Components;

public partial class OdsSortableList<TItem>
{
    /// <summary>The rows, in order.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<TItem> Items { get; set; } = [];

    /// <summary>A stable key per row that survives reordering, unique within the list — the row's <c>@key</c>, its
    /// handle's id and how a lift follows the row as it moves. Required: the item itself is not a safe default, since
    /// two equal values (two "salt" strings, two equal records) would share one key, and a value-type item's key would
    /// change with its content. Pass a per-row id (<c>RecipeRowDraft.Key</c>).</summary>
    [Parameter, EditorRequired] public Func<TItem, object> GetKey { get; set; } = default!;

    /// <summary>The row's name in lower case with its number — "ingredient 3". Used for the sr-only legend
    /// ("Ingredient 3"), the handle ("Move ingredient 3"), the menu ("Actions for ingredient 3") and the
    /// announcements. Default "item n".</summary>
    [Parameter] public Func<TItem, int, string>? GetLabel { get; set; }

    /// <summary>True for a heading row: a tinted band, flush left; the other rows step in once one exists.</summary>
    [Parameter] public Func<TItem, bool>? IsHeading { get; set; }

    /// <summary>The row's content between the handle and the ⋯ menu — several cells to fill the grid set by
    /// <see cref="RowClass"/>. Give every field its own label.</summary>
    [Parameter] public RenderFragment<OdsSortableItem<TItem>>? RenderItem { get; set; }

    /// <summary>Extra class per row — typically one setting <c>grid-template-columns</c> (handle 32px · your
    /// columns · menu 36px).</summary>
    [Parameter] public Func<TItem, int, string?>? RowClass { get; set; }

    /// <summary>Raised by every way of moving a row (drag, keys, menu). Reorder the list to match.</summary>
    [Parameter] public EventCallback<OdsSortMove> OnMove { get; set; }

    /// <summary>Adds Remove to the row menu. Omit for a list whose rows cannot be removed.</summary>
    [Parameter] public EventCallback<int> OnRemove { get; set; }

    /// <summary>The Remove item's label. Default "Remove heading" for a heading, else "Remove".</summary>
    [Parameter] public Func<TItem, string>? RemoveLabel { get; set; }

    /// <summary>Column names drawn once above the rows (aria-hidden), in the same grid. Not drawn on a phone.</summary>
    [Parameter] public RenderFragment? Header { get; set; }

    /// <summary>Extra class on the header row — the same grid columns as the rows.</summary>
    [Parameter] public string? HeaderClass { get; set; }

    /// <summary>The handle's description (aria-describedby).</summary>
    [Parameter] public string DragHint { get; set; } = "Drag to move. Or press Space, move with the arrow keys, and press Space again to drop.";

    [Parameter] public string? Class { get; set; }

    private enum DragMode { Pointer, Keys }

    private sealed record DragState(object Key, int From, DragMode Mode, int Over = -1, int Origin = -1);

    private readonly string _uid = $"odc-sort-{Guid.NewGuid():N}";
    private ElementReference _list;
    private DragState? _drag;
    private object? _settled;
    private string _say = string.Empty;
    private int _nonce;
    private object? _refocusKey;
    private IJSObjectReference? _module;
    private IJSObjectReference? _jsHandle;
    private DotNetObjectReference<OdsSortableList<TItem>>? _self;
    private readonly CancellationTokenSource _disposed = new();
    private bool _isDisposed;

    private string HelpId => $"{_uid}-help";

    private bool HasHeadings => IsHeading is not null && Items.Any(IsHeading);

    private string RootClass =>
        "odc-sortable" + (HasHeadings ? " has-headings" : "") + (string.IsNullOrEmpty(Class) ? "" : " " + Class);

    /// <summary>The text of the live region (with its re-announce padding stripped) — for tests.</summary>
    internal string Announcement => _say.TrimEnd('​');

    private object KeyOf(TItem item) =>
        (GetKey ?? throw new InvalidOperationException($"{nameof(OdsSortableList<TItem>)} needs {nameof(GetKey)}: a stable, unique key per row."))(item);

    private string LabelOf(TItem item, int index) =>
        GetLabel?.Invoke(item, index) ?? $"item {(index + 1).ToString(CultureInfo.InvariantCulture)}";

    private bool Heading(TItem item) => IsHeading?.Invoke(item) ?? false;

    private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    internal string HandleId(object key) =>
        $"{_uid}-h-{Regex.Replace(Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty, "[^A-Za-z0-9_-]", "_")}";

    private int IndexOfKey(object key)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (Equals(KeyOf(Items[i]), key))
            {
                return i;
            }
        }

        return -1;
    }

    private string RowClassFor(TItem item, int index, object key)
    {
        var n = Items.Count;
        var lifted = _drag is { } d && Equals(d.Key, key);
        var pointer = _drag is { Mode: DragMode.Pointer } p ? p : null;
        var noop = pointer is not null && (pointer.Over == pointer.From || pointer.Over == pointer.From + 1);
        var dropBefore = pointer is not null && !noop && pointer.Over == index;
        var dropAfter = pointer is not null && !noop && index == n - 1 && pointer.Over == n;
        var extra = RowClass?.Invoke(item, index);
        return "odc-sortable-row"
            + (Heading(item) ? " heading" : "")
            + (lifted ? " is-lifted" : "")
            + (lifted && _drag!.Mode == DragMode.Keys ? " is-raised" : "")
            + (Equals(_settled, key) ? " is-settled" : "")
            + (dropBefore ? " drop-before" : "")
            + (dropAfter ? " drop-after" : "")
            + (string.IsNullOrEmpty(extra) ? "" : " " + extra);
    }

    private IReadOnlyList<OdsMenuItem> MenuFor(TItem item, int index, string label)
    {
        var remove = RemoveLabel?.Invoke(item) ?? (Heading(item) ? "Remove heading" : "Remove");
        return
        [
            new OdsMenuItem { Icon = "arrow_upward", Label = "Move up", Disabled = index == 0, OnClick = EventCallback.Factory.Create(this, () => MenuMoveAsync(index, index - 1, label)) },
            new OdsMenuItem { Icon = "arrow_downward", Label = "Move down", Disabled = index == Items.Count - 1, OnClick = EventCallback.Factory.Create(this, () => MenuMoveAsync(index, index + 1, label)) },
            new OdsMenuItem { Divider = true, Disabled = !OnRemove.HasDelegate },
            new OdsMenuItem
            {
                Icon = "delete",
                Label = remove,
                AriaLabel = $"{remove} {label}",
                Danger = true,
                Disabled = !OnRemove.HasDelegate,
                OnClick = EventCallback.Factory.Create(this, () => OnRemove.InvokeAsync(index)),
            },
        ];
    }

    private async Task MoveAsync(int from, int to)
    {
        if (to == from || to < 0 || to >= Items.Count || from < 0 || from >= Items.Count)
        {
            return;
        }

        await OnMove.InvokeAsync(new OdsSortMove(from, to));
    }

    /// <summary>A Move up / Move down from the row's ⋯ menu: the same move, announced in the same live region as the
    /// keyboard lift (WCAG 4.1.3), since the menu is the documented alternative for assistive tech.</summary>
    private async Task MenuMoveAsync(int from, int to, string label)
    {
        if (to == from || to < 0 || to >= Items.Count || from < 0 || from >= Items.Count)
        {
            return;
        }

        var key = KeyOf(Items[from]);
        var n = Items.Count;
        await MoveAsync(from, to);
        Announce($"{Cap(label)} moved to position {to + 1} of {n}.");
        Settle(key);
    }

    private void Announce(string message)
    {
        // A live region ignores a change to identical text, so each message gets a different invisible tail.
        _nonce++;
        _say = message + new string('​', _nonce % 3 + 1);
    }

    private void Settle(object key)
    {
        _settled = key;
        _ = ClearSettledAsync(key);
    }

    private async Task ClearSettledAsync(object key)
    {
        // A JS DragEnd can land after DisposeAsync has disposed the source: reading its Token then throws
        // ObjectDisposedException inside this unobserved task, so check first and treat a disposed source as cancelled.
        if (_isDisposed)
        {
            return;
        }

        try
        {
            await Task.Delay(900, _disposed.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        if (_isDisposed)
        {
            return;
        }

        if (Equals(_settled, key))
        {
            _settled = null;
            await InvokeAsync(StateHasChanged);
        }
    }

    internal async Task OnHandleKeyDownAsync(KeyboardEventArgs e, int index)
    {
        if (index < 0 || index >= Items.Count)
        {
            return;
        }

        var item = Items[index];
        var key = KeyOf(item);
        var n = Items.Count;
        var lifted = _drag is { Mode: DragMode.Keys } d && Equals(d.Key, key);

        if (e.Key is " " or "Enter" or "Spacebar")
        {
            if (lifted)
            {
                _drag = null;
                Announce($"Dropped at position {index + 1} of {n}.");
                Settle(key);
            }
            else
            {
                _drag = new DragState(key, index, DragMode.Keys, Origin: index);
                Announce($"{Cap(LabelOf(item, index))} lifted, position {index + 1} of {n}. Arrow keys move it, Space drops it, Escape cancels.");
            }

            return;
        }

        if (!lifted)
        {
            return;
        }

        switch (e.Key)
        {
            case "ArrowUp":
            case "ArrowDown":
                var to = index + (e.Key == "ArrowUp" ? -1 : 1);
                if (to < 0 || to >= n)
                {
                    Announce(to < 0 ? "Already first." : "Already last.");
                    return;
                }

                await MoveAsync(index, to);
                Announce($"Position {to + 1} of {n}.");
                _refocusKey = key;
                break;
            case "Escape":
                // Normally handled by the JS module (which also stops the key reaching the dialog); this is the
                // path when the module is not attached.
                await CancelLift();
                break;
            case "Tab":
                // Tab drops the row where it is and moves on; say so, as Space does.
                _drag = null;
                Announce($"Dropped at position {index + 1} of {n}.");
                Settle(key);
                break;
        }
    }

    /// <summary>Escape on a lifted handle: put the row back where it started. Called by the JS module, which
    /// has already stopped the key so it does not also close an enclosing dialog.</summary>
    [JSInvokable]
    public async Task CancelLift()
    {
        if (_drag is not { Mode: DragMode.Keys } d)
        {
            return;
        }

        var now = IndexOfKey(d.Key);
        _drag = null;
        if (now >= 0 && now != d.Origin)
        {
            await MoveAsync(now, d.Origin);
        }

        Announce($"Move cancelled. Back at position {d.Origin + 1} of {Items.Count}.");
        _refocusKey = d.Key;
        StateHasChanged();
    }

    /// <summary>The pointer is over the gap before row <paramref name="over"/> (or after the last row).</summary>
    [JSInvokable]
    public void DragOver(int from, int over)
    {
        if (from < 0 || from >= Items.Count || _drag is { Mode: DragMode.Keys })
        {
            return;
        }

        _drag = new DragState(KeyOf(Items[from]), from, DragMode.Pointer, Over: over);
        StateHasChanged();
    }

    /// <summary>The pointer was released over the gap before row <paramref name="over"/>.</summary>
    [JSInvokable]
    public async Task DragEnd(int from, int over)
    {
        if (_drag is { Mode: DragMode.Keys })
        {
            return;
        }

        _drag = null;
        if (from < 0 || from >= Items.Count)
        {
            StateHasChanged();
            return;
        }

        var key = KeyOf(Items[from]);
        var to = over > from ? over - 1 : over;
        if (to != from && to >= 0 && to < Items.Count)
        {
            await MoveAsync(from, to);
            Settle(key);
        }

        StateHasChanged();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                _module = await Js.InvokeAsync<IJSObjectReference>("import", "./js/sortable-list.js");
                _self = DotNetObjectReference.Create(this);
                if (_module is not null)
                {
                    _jsHandle = await _module.InvokeAsync<IJSObjectReference>("attach", _list, _self);
                }
            }
            catch (JSException) { /* no module (prerender); keys still work, drag does not */ }
            catch (InvalidOperationException) { /* JS unavailable */ }
        }

        if (_refocusKey is { } key)
        {
            _refocusKey = null;
            try
            {
                // On a keyboard move the handle keeps focus (it was re-ordered in the DOM).
                await Js.InvokeVoidAsync("odsFocusById", HandleId(key));
            }
            catch (JSException) { /* gone meanwhile */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        await _disposed.CancelAsync();
        _disposed.Dispose();
        try
        {
            if (_module is not null)
            {
                if (_jsHandle is not null)
                {
                    await _module.InvokeVoidAsync("detach", _jsHandle);
                    await _jsHandle.DisposeAsync();
                }

                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException) { /* circuit gone */ }
        catch (JSException) { /* already torn down */ }
        catch (TaskCanceledException) { /* teardown */ }

        _self?.Dispose();
    }
}
