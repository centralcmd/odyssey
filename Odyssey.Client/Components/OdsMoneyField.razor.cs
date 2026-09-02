using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;

namespace Odyssey.Client.Components;

public partial class OdsMoneyField
{
    /// <summary>Visible label, rendered above the control and tied to it via <c>for</c>.</summary>
    [Parameter] public string? Label { get; set; }

    /// <summary>The amount, kept as a string so partial entries aren't clobbered. Bindable via @bind-Value.</summary>
    [Parameter] public string? Value { get; set; }

    [Parameter] public EventCallback<string> ValueChanged { get; set; }

    /// <summary>ISO 4217 code shown on the right, inside the same box ("NOK", "USD").</summary>
    [Parameter] public string? Currency { get; set; }

    /// <summary>Fires with the picked ISO code. Omit to render the code as static text.</summary>
    [Parameter] public EventCallback<string> CurrencyChanged { get; set; }

    /// <summary>The selectable currencies — <c>Value</c> is the ISO code, <c>Label</c> its name.</summary>
    [Parameter] public IReadOnlyList<OdsOption> CurrencyOptions { get; set; } = [];

    /// <summary>Set false to lock the currency (an account currency, a base currency) — the code
    /// renders as static text. Default true.</summary>
    [Parameter] public bool CurrencyEditable { get; set; } = true;

    /// <summary>Locks only the currency while the amount stays editable.</summary>
    [Parameter] public bool CurrencyDisabled { get; set; }

    /// <summary>Shown in place of the code when <see cref="Currency"/> is empty.</summary>
    [Parameter] public string CurrencyPlaceholder { get; set; } = "—";

    /// <summary>Option count above which the picker shows a search box (matching code or name).
    /// Default 8; 0 to always search.</summary>
    [Parameter] public int CurrencySearchThreshold { get; set; } = 8;

    [Parameter] public string Placeholder { get; set; } = "0.00";

    /// <summary>"md" (default, data-entry rows) or "lg" (a large, focal hero input).</summary>
    [Parameter] public string Size { get; set; } = "md";

    /// <summary>"left" (default) or "right" text alignment for the amount.</summary>
    [Parameter] public string Align { get; set; } = "left";

    /// <summary>Leading sign glyph inside the box ("−", "+") for a signed amount whose direction
    /// is set elsewhere in the form.</summary>
    [Parameter] public string? Sign { get; set; }

    /// <summary>Current direction. With <see cref="DirectionChanged"/> the leading segment becomes a
    /// button that flips expense ↔ income, and drives the sign and tone itself.</summary>
    [Parameter] public string? Direction { get; set; }

    /// <summary>Fires with the next direction when the leading segment is clicked — or when − / + is
    /// typed in the amount. Omit for a read-only sign.</summary>
    [Parameter] public EventCallback<string> DirectionChanged { get; set; }

    /// <summary>Turns the leading segment into a − / + toggle over the value's own sign, for a signed
    /// amount with no income/expense meaning (a correction, an adjustment). The minus is picked, not
    /// typed; <see cref="Value"/> stays signed.</summary>
    [Parameter] public bool SignEditable { get; set; }

    /// <summary>Colors the sign and amount by direction, using the finance income / expense hues.</summary>
    [Parameter] public string? Tone { get; set; }

    /// <summary>Accept a leading minus — refunds, corrections, negative adjustments. Default true;
    /// set false where a negative is meaningless.</summary>
    [Parameter] public bool AllowNegative { get; set; } = true;

    [Parameter] public string? Help { get; set; }

    /// <summary>Rich helper content (takes precedence over <see cref="Help"/>), e.g. a hint with bold.</summary>
    [Parameter] public RenderFragment? HelpContent { get; set; }

    [Parameter] public string? Error { get; set; }

    [Parameter] public bool Required { get; set; }

    [Parameter] public bool Optional { get; set; }

    /// <summary>Disables the whole control (amount and currency).</summary>
    [Parameter] public bool Disabled { get; set; }

    [Parameter] public bool AutoFocus { get; set; }

    [Parameter] public string? Class { get; set; }

    [Parameter] public string? Id { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? UserAttributes { get; set; }

    private MudMenu? _menu;
    private bool _open;
    private bool _focusPending;
    private string _query = string.Empty;

    private string FieldId = default!;

    private string TriggerId => $"{FieldId}-currency";
    private string SearchId => $"{FieldId}-currency-search";
    private string ListId => $"{FieldId}-currency-listbox";
    private string OptionId(int index) => $"{FieldId}-currency-opt-{index}";

    protected override void OnInitialized() => FieldId = Id ?? $"odc-money-{Guid.NewGuid():N}";

    // ---- modes ----------------------------------------------------------------------------

    /// <summary>The leading segment flips expense ↔ income (the transaction hero).</summary>
    private bool DirectionMode => !string.IsNullOrEmpty(Direction) && DirectionChanged.HasDelegate;

    /// <summary>The leading segment flips the value's own minus (a correction, a settlement).</summary>
    private bool SignMode => !DirectionMode && SignEditable && ValueChanged.HasDelegate;

    private bool CurrencyPickable =>
        CurrencyEditable && !Disabled && !CurrencyDisabled
        && CurrencyOptions.Count > 0 && CurrencyChanged.HasDelegate;

    private bool Searchable => CurrencyOptions.Count > Math.Max(CurrencySearchThreshold, 0);

    // ---- value / sign ---------------------------------------------------------------------

    private bool Negative => Value is not null && Value.TrimStart().StartsWith('-');

    private string Magnitude => (Value ?? string.Empty).TrimStart().TrimStart('-');

    private string? DisplayValue => SignMode ? Magnitude : Value;

    private string? EffectiveTone => Tone ?? (DirectionMode ? Direction : null);

    private string? SignGlyph
    {
        get
        {
            if (!string.IsNullOrEmpty(Sign)) return Sign;
            if (!string.IsNullOrEmpty(Direction)) return Direction == "expense" ? "−" : "+";
            return SignMode ? (Negative ? "−" : "+") : null;
        }
    }

    private string SignAriaLabel => DirectionMode
        ? (Direction == "expense" ? "Expense — switch to income" : "Income — switch to expense")
        : (Negative ? "Negative — switch to positive" : "Positive — switch to negative");

    private string SignTitle => DirectionMode
        ? (Direction == "expense" ? "Expense — click to switch" : "Income — click to switch")
        : (Negative ? "Negative — click to switch" : "Positive — click to switch");

    // ---- currency -------------------------------------------------------------------------

    private string CurrencyCode => string.IsNullOrEmpty(Currency) ? CurrencyPlaceholder : Currency;

    private string CurrencyAriaLabel =>
        string.IsNullOrEmpty(Currency) ? "Currency" : $"Currency: {Currency}";

    private IReadOnlyList<OdsOption> Shown
    {
        get
        {
            var q = _query.Trim();
            if (q.Length == 0) return CurrencyOptions;
            return [.. CurrencyOptions.Where(o =>
                o.Value.Contains(q, StringComparison.OrdinalIgnoreCase)
                || o.Label.Contains(q, StringComparison.OrdinalIgnoreCase))];
        }
    }

    // ---- classes --------------------------------------------------------------------------

    private string BoxClass =>
        string.Join(' ', new[]
        {
            "odc-money",
            Size == "lg" ? "lg" : null,
            string.IsNullOrEmpty(EffectiveTone) ? null : $"tone-{EffectiveTone}",
            string.IsNullOrEmpty(Error) ? null : "error",
            Disabled ? "disabled" : null,
        }.Where(value => !string.IsNullOrEmpty(value)));

    private string TriggerClass => _open ? "odc-money-cur btn open" : "odc-money-cur btn";

    private string? DescribedBy
    {
        get
        {
            var hasHelp = HelpContent is not null || !string.IsNullOrEmpty(Help);
            var hasError = !string.IsNullOrEmpty(Error);
            if (!hasHelp && !hasError) return null;
            // OdsFieldShell gives the error its own id only when a help node renders alongside it.
            return hasHelp && hasError ? $"{FieldId}-help {FieldId}-help-error" : $"{FieldId}-help";
        }
    }

    // ---- amount input ---------------------------------------------------------------------

    /// <summary>
    /// The keystroke rules, shared with the file-analysis grid's inline amount cell so the two can
    /// never disagree — see <see cref="OdsMoneyText"/>.
    /// </summary>
    private string? Sanitize(string raw) => OdsMoneyText.Sanitize(raw, AllowNegative);

    private async Task OnAmountInputAsync(ChangeEventArgs e)
    {
        var raw = e.Value?.ToString() ?? string.Empty;
        var next = Sanitize(raw);

        // A rejected keystroke leaves Value unchanged, so Blazor's diff sees nothing to write and the
        // character would linger in the DOM — put the field back to what it holds.
        if (next is null)
        {
            await RestoreInputAsync(DisplayValue ?? string.Empty);
            return;
        }

        // In sign mode the input shows the magnitude and the leading segment owns the minus, so a
        // typed minus never reaches the value — OnAmountKeyDownAsync has already flipped the sign.
        var committed = SignMode ? (Negative ? "-" : string.Empty) + next.TrimStart().TrimStart('-') : next;
        var display = SignMode ? next.TrimStart().TrimStart('-') : next;

        Value = committed;
        await ValueChanged.InvokeAsync(committed);

        if (display != raw) await RestoreInputAsync(display);
    }

    private async Task OnAmountKeyDownAsync(KeyboardEventArgs e)
    {
        var minus = e.Key is "-" or "−";
        var plus = e.Key == "+";
        if (!minus && !plus) return;

        if (DirectionMode)
        {
            var next = minus ? "expense" : "income";
            if (next != Direction)
            {
                Direction = next;
                await DirectionChanged.InvokeAsync(next);
            }
            return;
        }

        if (!SignMode) return;

        var signed = (minus ? "-" : string.Empty) + Magnitude;
        if (signed == Value) return;
        Value = signed;
        await ValueChanged.InvokeAsync(signed);
    }

    private async Task FlipSignAsync()
    {
        if (DirectionMode)
        {
            var next = Direction == "expense" ? "income" : "expense";
            Direction = next;
            await DirectionChanged.InvokeAsync(next);
            return;
        }

        var signed = Negative ? Magnitude : "-" + Magnitude;
        Value = signed;
        await ValueChanged.InvokeAsync(signed);
    }

    // ---- currency picker ------------------------------------------------------------------

    private void OnMenuOpenChanged(bool open)
    {
        _open = open;
        if (open)
        {
            _query = string.Empty;
            _focusPending = true;
        }
    }

    private async Task PickAsync(string code)
    {
        Currency = code;
        await CurrencyChanged.InvokeAsync(code);
        if (_menu is not null) await _menu.CloseMenuAsync();
        await FocusAsync(TriggerId);
    }

    // The popover opens on the search box when there is one, otherwise on the selected code — so a
    // keyboard user lands where the next keystroke does something useful either way.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_focusPending || !_open) return;
        _focusPending = false;

        if (Searchable) { await FocusAsync(SearchId); return; }
        var index = Shown.ToList().FindIndex(o => string.Equals(o.Value, Currency, StringComparison.OrdinalIgnoreCase));
        await FocusAsync(OptionId(Math.Max(index, 0)));
    }

    private async Task OnSearchKeyDownAsync(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "ArrowDown":
                await FocusAsync(OptionId(0));
                break;
            case "Enter":
                var first = Shown.FirstOrDefault();
                if (first is not null) await PickAsync(first.Value);
                break;
            case "Escape":
                if (_menu is not null) await _menu.CloseMenuAsync();
                await FocusAsync(TriggerId);
                break;
        }
    }

    private async Task OnListKeyDownAsync(KeyboardEventArgs e)
    {
        // Roving focus over the option buttons: the listbox has one tab stop, and Home / End reach
        // the ends of a list long enough that arrowing to them would be a chore.
        var index = await ActiveOptionIndexAsync();
        switch (e.Key)
        {
            case "ArrowDown":
                await FocusAsync(OptionId(Math.Min(index + 1, Shown.Count - 1)));
                break;
            case "ArrowUp":
                if (index <= 0 && Searchable) await FocusAsync(SearchId);
                else await FocusAsync(OptionId(Math.Max(index - 1, 0)));
                break;
            case "Home":
                await FocusAsync(OptionId(0));
                break;
            case "End":
                await FocusAsync(OptionId(Shown.Count - 1));
                break;
            case "Escape":
                if (_menu is not null) await _menu.CloseMenuAsync();
                await FocusAsync(TriggerId);
                break;
        }
    }

    private async Task<int> ActiveOptionIndexAsync()
    {
        try
        {
            var id = await Js.InvokeAsync<string?>("odsActiveElementId");
            if (string.IsNullOrEmpty(id)) return -1;
            var prefix = $"{FieldId}-currency-opt-";
            return id.StartsWith(prefix, StringComparison.Ordinal)
                   && int.TryParse(id[prefix.Length..], out var index)
                ? index
                : -1;
        }
        catch
        {
            // JS unavailable (prerender / teardown) — arrowing simply starts from the top.
            return -1;
        }
    }

    private async Task FocusAsync(string id)
    {
        try { await Js.InvokeVoidAsync("odsFocusById", id); }
        catch { /* JS unavailable (e.g. prerender / teardown) */ }
    }

    private async Task RestoreInputAsync(string value)
    {
        try { await Js.InvokeVoidAsync("odsSetInputValue", FieldId, value); }
        catch { /* JS unavailable (e.g. prerender / teardown) */ }
    }
}
