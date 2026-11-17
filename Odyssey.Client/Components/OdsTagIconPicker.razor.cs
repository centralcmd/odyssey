using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Components;

public partial class OdsTagIconPicker : IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>The selected catalogue key, or <c>null</c> for Default. Bindable via @bind-Value.</summary>
    [Parameter] public string? Value { get; set; }

    [Parameter] public EventCallback<string?> ValueChanged { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter] public string? Id { get; set; }

    [Parameter] public string AriaLabel { get; set; } = "Icon";

    /// <summary>The id of a visible label (OdsFieldShell's LabelId); replaces <see cref="AriaLabel"/>.</summary>
    [Parameter] public string? AriaLabelledBy { get; set; }

    [Parameter] public string? AriaDescribedBy { get; set; }

    /// <summary>Marks the group <c>aria-invalid</c> while the field shows an error (WCAG 3.3.1).</summary>
    [Parameter] public bool Invalid { get; set; }

    [Parameter] public string? Class { get; set; }

    /// <summary>
    /// The glyph of the Default cell, its caption and peek (issue #406). Rendered only as text inside a
    /// <c>material-icons</c> span. An album passes <see cref="PhotoAlbumIcons.Default"/>.
    /// </summary>
    [Parameter] public string DefaultIcon { get; set; } = TagIcons.Default;

    /// <summary>The Default cell's accessible name. An album passes "Default (album icon)".</summary>
    [Parameter] public string DefaultLabel { get; set; } = "Default (tag icon)";

    /// <summary>The caption sub-label beside "Default" (the caption is aria-hidden). An album passes "the album icon".</summary>
    [Parameter] public string DefaultCaption { get; set; } = "the tag icon";

    /// <summary>The search box's accessible name.</summary>
    [Parameter] public string SearchLabel { get; set; } = "Search icons";

    /// <summary>Used when the live column count cannot be read (prerender, a failed import).</summary>
    internal const int FallbackColumns = 8;

    // A null key is the Default cell.
    private sealed record Cell(string? Key, string Label);

    private static readonly IReadOnlyList<Cell> Cells =
        [new Cell(null, "Default"), .. TagIcons.All.Select(option => new Cell(option.Key, option.Label))];

    private IReadOnlyList<Cell> _visible = Cells;
    private readonly ElementReference[] _buttons = new ElementReference[Cells.Count];
    private ElementReference _grid;
    private ElementReference _search;
    private string _query = "";
    private string? _announcement;
    private bool _focusSelectedAfterRender;
    private IJSObjectReference? _module;
    private IJSObjectReference? _handle;
    private int? _peek;
    private int? _focused;
    private string _groupId = default!;

    private string RootClass =>
        $"odc-iconpick{(Disabled ? " disabled" : "")}{(string.IsNullOrWhiteSpace(Class) ? "" : " " + Class)}";

    private static string CellClass(bool active, bool isDefault) =>
        $"odc-iconpick-cell{(active ? " selected" : "")}{(isDefault ? " default" : "")}";

    private bool Searching => _query.Trim().Length > 0;

    // An unknown or stale key selects Default, exactly as the server projects it.
    private Cell Selected
    {
        get
        {
            var key = TagIcons.Normalize(Value);
            return key is null ? Cells[0] : Cells.First(cell => string.Equals(cell.Key, key, StringComparison.Ordinal));
        }
    }

    // The selected cell's position among the visible cells; -1 while a search hides it.
    private int SelectedIndex
    {
        get
        {
            var key = Selected.Key;
            for (var i = 0; i < _visible.Count; i++)
            {
                if (string.Equals(_visible[i].Key, key, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    // The roving tab stop: the selected cell, or the first match when a search hides it.
    private int TabStopIndex => Math.Max(0, SelectedIndex);

    private Cell? Peeked =>
        _peek is { } i && i != SelectedIndex && i >= 0 && i < _visible.Count ? _visible[i] : null;

    private string StatusText => _announcement ?? (!Searching
        ? ""
        : _visible.Count switch
        {
            0 => "No icons match.",
            1 => "1 icon matches.",
            var n => $"{n} icons match.",
        });

    protected override void OnInitialized() => _groupId = Id ?? $"odc-iconpick-{Guid.NewGuid():N}";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // A query that hid the selection was cleared by FocusSelectedAsync; the cell exists only now.
        if (_focusSelectedAfterRender)
        {
            _focusSelectedAfterRender = false;
            await FocusCellAsync(Math.Max(0, SelectedIndex));
        }

        if (!firstRender || !OperatingSystem.IsBrowser())
        {
            return;
        }

        try
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/icon-picker.js");
            _handle = await _module.InvokeAsync<IJSObjectReference>("attach", _grid, _search);
        }
        catch (JSException)
        {
            // Keyboard still works without it; the arrows merely scroll and Up/Down step a fixed row.
        }
    }

    private Task PickAsync(int index)
    {
        if (Disabled)
        {
            return Task.CompletedTask;
        }

        Value = _visible[index].Key;
        return ValueChanged.InvokeAsync(Value);
    }

    private async Task OnKeyDownAsync(KeyboardEventArgs e, int index)
    {
        var last = _visible.Count - 1;
        int? next = e.Key switch
        {
            "ArrowRight" => Math.Min(last, index + 1),
            "ArrowLeft" => Math.Max(0, index - 1),
            "ArrowDown" => Math.Min(last, index + await ColumnsAsync()),
            "ArrowUp" => Math.Max(0, index - await ColumnsAsync()),
            "Home" => 0,
            "End" => last,
            // Space and Enter are left to the button's native click, which already picks the cell.
            _ => null,
        };

        if (next is not { } target)
        {
            return;
        }

        await PickAsync(target);
        if (target != index)
        {
            _peek = target;
            _focused = target;
            await _buttons[target].FocusAsync();
        }
    }

    private async Task<int> ColumnsAsync()
    {
        if (_module is null)
        {
            return FallbackColumns;
        }

        try
        {
            var columns = await _module.InvokeAsync<int>("columns", _grid);
            return columns > 0 ? columns : FallbackColumns;
        }
        catch (JSException)
        {
            return FallbackColumns;
        }
    }

    private void OnQueryInput(ChangeEventArgs e) => SetQuery(e.Value as string);

    private void SetQuery(string? query)
    {
        _query = query ?? "";
        _visible = Searching
            ? TagIconSearch.Search(_query).Select(option => new Cell(option.Key, option.Label)).ToList()
            : Cells;
        _peek = _focused = null;
        _announcement = null;
    }

    // icon-picker.js stops the defaults of Arrow Down and Enter. Escape on a query never reaches here in a
    // browser: the bridge stops it before the dialog sees it and clears the box through an input event.
    // The Escape case below serves a host without the bridge.
    private async Task OnSearchKeyDownAsync(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "ArrowDown" when _visible.Count > 0:
                await FocusCellAsync(TabStopIndex);
                break;
            case "Enter" when Searching && _visible.Count > 0 && !Disabled:
                await PickAsync(0);
                // Focus stays in the box and the caption is aria-hidden, so the pick is announced.
                _announcement = $"{_visible[0].Label} selected.";
                break;
            case "Escape" when _query.Length > 0:
                SetQuery("");
                break;
        }
    }

    private async Task FocusCellAsync(int index)
    {
        try
        {
            await _buttons[index].FocusAsync();
        }
        catch (InvalidOperationException)
        {
        }
        catch (JSException)
        {
        }
    }

    /// <summary>Moves focus to the selected cell — the target of a server error on this field (issue #406 §7).</summary>
    /// <remarks>
    /// A query that hides the selection is cleared first, and the focus then waits for the render that
    /// draws the cell: the element references still name the filtered grid until it runs.
    /// </remarks>
    public async Task FocusSelectedAsync()
    {
        if (SelectedIndex < 0)
        {
            SetQuery("");
            _focusSelectedAfterRender = true;
            StateHasChanged();
            return;
        }

        await FocusCellAsync(SelectedIndex);
    }

    // The caption previews the hovered cell, falling back to the focused one when the pointer leaves.
    private void OnCellFocus(int index) => _peek = _focused = index;

    private void OnCellBlur() => _peek = _focused = null;

    private void OnGridMouseLeave() => _peek = _focused;

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_handle is not null)
            {
                await _handle.InvokeVoidAsync("detach");
                await _handle.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (JSException)
        {
        }
    }
}
