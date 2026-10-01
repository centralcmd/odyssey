using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
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

    [Parameter] public string? Class { get; set; }

    /// <summary>Used when the live column count cannot be read (prerender, a failed import).</summary>
    internal const int FallbackColumns = 8;

    // A null key is the Default cell.
    private sealed record Cell(string? Key, string Label);

    private static readonly IReadOnlyList<Cell> Cells =
        [new Cell(null, "Default"), .. TransactionTagIcons.All.Select(option => new Cell(option.Key, option.Label))];

    private readonly IReadOnlyList<Cell> _cells = Cells;
    private readonly ElementReference[] _buttons = new ElementReference[Cells.Count];
    private ElementReference _grid;
    private IJSObjectReference? _module;
    private IJSObjectReference? _handle;
    private int? _peek;
    private string _groupId = default!;

    private string RootClass =>
        $"odc-iconpick{(Disabled ? " disabled" : "")}{(string.IsNullOrWhiteSpace(Class) ? "" : " " + Class)}";

    private static string CellClass(bool active, bool isDefault) =>
        $"odc-iconpick-cell{(active ? " selected" : "")}{(isDefault ? " default" : "")}";

    // An unknown or stale key selects Default, exactly as the server projects it.
    private int SelectedIndex
    {
        get
        {
            var key = TransactionTagIcons.Normalize(Value);
            if (key is null)
            {
                return 0;
            }

            for (var i = 1; i < _cells.Count; i++)
            {
                if (string.Equals(_cells[i].Key, key, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return 0;
        }
    }

    private Cell Selected => _cells[SelectedIndex];

    private Cell? Peeked =>
        _peek is { } i && i != SelectedIndex && i >= 0 && i < _cells.Count ? _cells[i] : null;

    protected override void OnInitialized() => _groupId = Id ?? $"odc-iconpick-{Guid.NewGuid():N}";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || !OperatingSystem.IsBrowser())
        {
            return;
        }

        try
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/icon-picker.js");
            _handle = await _module.InvokeAsync<IJSObjectReference>("attach", _grid);
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

        Value = _cells[index].Key;
        return ValueChanged.InvokeAsync(Value);
    }

    private async Task OnKeyDownAsync(KeyboardEventArgs e, int index)
    {
        var last = _cells.Count - 1;
        int? next = e.Key switch
        {
            "ArrowRight" => Math.Min(last, index + 1),
            "ArrowLeft" => Math.Max(0, index - 1),
            "ArrowDown" => Math.Min(last, index + await ColumnsAsync()),
            "ArrowUp" => Math.Max(0, index - await ColumnsAsync()),
            "Home" => 0,
            "End" => last,
            " " or "Enter" => index,
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
