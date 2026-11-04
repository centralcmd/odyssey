using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Odyssey.Client.Theme;
using Odyssey.Dtos;

namespace Odyssey.Client.Components;

public partial class OdsTagColorPicker : IAsyncDisposable
{
    /// <summary>The CustomEmpty message, shown on submit (issue #394 §3).</summary>
    public const string EmptyMessage = "Enter a 6-digit hex color, such as #0369A1, or choose another option.";

    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>Stored colour (any case) or <c>null</c>. Read once when the picker opens (the dialog re-keys it per open).</summary>
    [Parameter] public string? Value { get; set; }

    /// <summary>Fires with the canonical hex or <c>null</c> (No color). Not fired while Custom is empty or invalid.</summary>
    [Parameter] public EventCallback<string?> ValueChanged { get; set; }

    /// <summary>Fires false in CustomEmpty / CustomInvalid, so the host blocks submit; true otherwise.</summary>
    [Parameter] public EventCallback<bool> ValidChanged { get; set; }

    /// <summary>While saving: <c>aria-disabled</c>, input ignored — never <c>disabled</c>, so focus is kept.</summary>
    [Parameter] public bool Disabled { get; set; }

    /// <summary>A server <c>400</c> on Color while a swatch or No color is selected: marks the group invalid.</summary>
    [Parameter] public bool Invalid { get; set; }

    /// <summary>A server error message for the hex field.</summary>
    [Parameter] public string? Error { get; set; }

    /// <summary>After a failed submit: show the CustomEmpty / CustomInvalid message (no alert role; focus reads it).</summary>
    [Parameter] public bool ShowErrors { get; set; }

    [Parameter] public string? Id { get; set; }

    [Parameter] public string AriaLabel { get; set; } = "Color";

    [Parameter] public string? AriaLabelledBy { get; set; }

    /// <summary>The help / error ids describing the group.</summary>
    [Parameter] public string? AriaDescribedBy { get; set; }

    [Parameter] public string? Class { get; set; }

    internal const int FallbackColumns = 10;

    private static readonly IReadOnlyList<ColorSwatch> Swatches = ColorPalette.All;
    private static int CellCount => Swatches.Count + 2;
    private static int CustomIndex => Swatches.Count + 1;

    private readonly ElementReference[] _buttons = new ElementReference[CellCount];
    private ElementReference _grid;
    private OdsTextInputField? _hexField;
    private IJSObjectReference? _module;
    private IJSObjectReference? _handle;
    private string _groupId = default!;
    private string _hintId = default!;
    private int _sel;
    private string _draft = string.Empty;
    private string? _lastSwatch;
    private string? _stored;
    private string? _blurError;
    private bool _lastReportedValid = true;

    private string HexId => $"{_groupId}-hex";

    private string RootClass =>
        string.Join(' ', new[] { "odc-colorpick", Disabled ? "disabled" : null, Class }.Where(c => !string.IsNullOrEmpty(c)));

    /// <summary>NoColor · Swatch · CustomEmpty · CustomValid · CustomInvalid.</summary>
    internal string State => _sel == 0 ? "NoColor"
        : _sel < CustomIndex ? "Swatch"
        : _draft.Length == 0 ? "CustomEmpty"
        : UserColor.Canonical(_draft) is not null ? "CustomValid" : "CustomInvalid";

    private string? SubmitError => !ShowErrors ? null
        : State == "CustomEmpty" ? EmptyMessage
        : State == "CustomInvalid" ? HexColor.InvalidMessage : null;

    private string? FieldError => Error ?? SubmitError ?? _blurError;

    // Only a blur-time error is an alert; a submit-time or server error is read when focus moves to the field.
    private bool FieldErrorIsAlert => Error is null && SubmitError is null && _blurError is not null;

    private bool GroupInvalid => Invalid && _sel != CustomIndex;

    private string GroupErrorId => $"{_groupId}-error";

    private string? GroupDescribedBy =>
        string.Join(' ', new[] { AriaDescribedBy, GroupInvalid && !string.IsNullOrEmpty(Error) ? GroupErrorId : null }
            .Where(id => !string.IsNullOrEmpty(id))) is { Length: > 0 } ids ? ids : null;

    private string Caption => State switch
    {
        "NoColor" => "No color",
        "Swatch" => Swatches[_sel - 1].Name,
        "CustomValid" => $"Custom · {UserColor.Canonical(_draft)}",
        "CustomInvalid" => "Custom · not a valid color",
        _ => "Custom · no color entered",
    };

    private string? CaptionDot => State switch
    {
        "Swatch" => UserColor.Dot(Swatches[_sel - 1].Hex),
        "CustomValid" => UserColor.Dot(_draft),
        _ => null,
    };

    private string WellValue => (UserColor.Canonical(_draft) ?? "#000000").ToLowerInvariant();

    protected override void OnInitialized()
    {
        _groupId = Id ?? $"odc-colorpick-{Guid.NewGuid():N}";
        _hintId = $"{_groupId}-custom-hint";
        _stored = UserColor.Canonical(Value);
        var swatchIndex = _stored is null ? -1 : Swatches.ToList().FindIndex(s => s.Hex == _stored);
        if (_stored is null)
        {
            _sel = 0;
        }
        else if (swatchIndex >= 0)
        {
            _sel = swatchIndex + 1;
            _lastSwatch = Swatches[swatchIndex].Hex;
        }
        else
        {
            _sel = CustomIndex;
            _draft = _stored;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
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
            // Keyboard still works without the bridge; arrows may also scroll the dialog.
        }
    }

    internal static string CellName(int index) =>
        index == 0 ? "No color" : index < CustomIndex ? Swatches[index - 1].Name : string.Empty;

    private string CellNameFor(int index) => index == CustomIndex ? CustomName : CellName(index);

    private string CustomName => State switch
    {
        "CustomValid" => $"Custom, {UserColor.Canonical(_draft)}",
        "CustomInvalid" => "Custom, not a valid color",
        _ => "Custom, no color entered",
    };

    private static string CellKey(int index) =>
        index == 0 ? "__none" : index < CustomIndex ? Swatches[index - 1].Key : "__custom";

    private string CellClass(int index, bool active) => string.Join(' ', new[]
    {
        "odc-colorpick-cell",
        active ? "selected" : null,
        index == 0 ? "none" : null,
        index == CustomIndex ? "custom" : null,
        CellStyle(index) is null ? null : "filled",
    }.Where(c => c is not null));

    private string? CellStyle(int index) =>
        index == 0 ? null : index < CustomIndex ? UserColor.Swatch(Swatches[index - 1].Hex) : UserColor.Swatch(_draft);

    private async Task ChooseAsync(int index)
    {
        if (Disabled)
        {
            return;
        }

        _sel = index;
        _blurError = null;
        if (index == 0)
        {
            await EmitAsync(null, valid: true);
        }
        else if (index < CustomIndex)
        {
            _lastSwatch = Swatches[index - 1].Hex;
            await EmitAsync(_lastSwatch, valid: true);
        }
        else
        {
            // Seed: this session's draft, else the previously selected swatch, else the stored colour.
            if (_draft.Length == 0)
            {
                _draft = _lastSwatch ?? _stored ?? string.Empty;
            }

            await EmitCustomAsync();
        }
    }

    private Task EmitCustomAsync() =>
        UserColor.Canonical(_draft) is { } canonical ? EmitAsync(canonical, valid: true) : ReportValidAsync(false);

    private async Task EmitAsync(string? value, bool valid)
    {
        await ValueChanged.InvokeAsync(value);
        await ReportValidAsync(valid);
    }

    private Task ReportValidAsync(bool valid)
    {
        if (valid == _lastReportedValid)
        {
            return Task.CompletedTask;
        }

        _lastReportedValid = valid;
        return ValidChanged.InvokeAsync(valid);
    }

    private Task OnHexInputAsync(string? raw)
    {
        if (Disabled)
        {
            return Task.CompletedTask;
        }

        _draft = raw ?? string.Empty;
        _blurError = null;
        var candidate = FromInput(_draft);
        return UserColor.Canonical(candidate) is { } canonical ? EmitAsync(canonical, valid: true) : ReportValidAsync(false);
    }

    private void OnHexBlur()
    {
        var candidate = FromInput(_draft);
        if (UserColor.Canonical(candidate) is { } canonical)
        {
            _draft = canonical;
            _blurError = null;
        }
        else
        {
            _draft = candidate;
            _blurError = candidate.Length == 0 ? null : HexColor.InvalidMessage;
        }
    }

    // The native well commits on `change`, not on every `input` (issue #394 §3).
    private Task OnWellChangedAsync(ChangeEventArgs e)
    {
        if (Disabled || UserColor.Canonical(e.Value?.ToString()) is not { } canonical)
        {
            return Task.CompletedTask;
        }

        _sel = CustomIndex;
        _draft = canonical;
        _blurError = null;
        return EmitAsync(canonical, valid: true);
    }

    /// <summary>
    /// The field's convenience (issue #394 §8): trim ASCII whitespace, then prefix <c>#</c> only when that
    /// makes the value valid. Not a second hex check — the shared <see cref="HexColor.IsValid"/> decides.
    /// </summary>
    internal static string FromInput(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim(' ', '\t', '\r', '\n');
        return trimmed.Length > 0 && trimmed[0] != '#' && HexColor.IsValid($"#{trimmed}") ? $"#{trimmed}" : trimmed;
    }

    private async Task OnKeyDownAsync(KeyboardEventArgs e, int index)
    {
        var last = CellCount - 1;
        int? target = e.Key switch
        {
            "ArrowRight" => (index + 1) % CellCount,
            "ArrowLeft" => (index - 1 + CellCount) % CellCount,
            "ArrowDown" => Math.Min(last, index + await ColumnsAsync()),
            "ArrowUp" => Math.Max(0, index - await ColumnsAsync()),
            "Home" => 0,
            "End" => last,
            _ => null,
        };

        if (target is not { } next || next == index || Disabled)
        {
            return;
        }

        await ChooseAsync(next);
        await _buttons[next].FocusAsync();
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

    /// <summary>Focuses the selected cell — after a server <c>400</c> while a swatch or No color is selected.</summary>
    public ValueTask FocusSelectedAsync() => _buttons[_sel].FocusAsync();

    /// <summary>Focuses the hex field when Custom is selected, else the selected cell.</summary>
    public ValueTask FocusErrorAsync() => _sel == CustomIndex && _hexField is not null ? _hexField.FocusAsync() : FocusSelectedAsync();

    /// <summary>True while Custom holds no valid colour — the host must block submit.</summary>
    public bool BlocksSubmit => State is "CustomEmpty" or "CustomInvalid";

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

        GC.SuppressFinalize(this);
    }
}
