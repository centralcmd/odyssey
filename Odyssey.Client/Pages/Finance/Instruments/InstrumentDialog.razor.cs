using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance.Instruments;

/// <summary>New / Edit instrument: parameters, validation and the save path. The markup is in <c>InstrumentDialog.razor</c>.</summary>
public partial class InstrumentDialog
{
    // The field messages say how to fix the problem (issue #452 §6.8); the format ones are the DTO's own.
    internal const string SymbolRequired = "Enter the symbol.";
    internal const string SymbolFormat = "Use letters, digits, dots, hyphens and spaces, starting with a letter or digit.";
    internal const string MicFormat = "Enter the 4-character exchange code, e.g. XOSL for Oslo Børs or XNAS for Nasdaq.";
    internal const string IsinRequired = "A fund needs its ISIN.";
    internal const string IsinFormat = "Enter 12 characters: 2 country letters, 9 letters or digits, then 1 check digit.";
    internal const string IsinCheckDigit = "This ISIN's check digit doesn't match. Check the last character.";
    internal const string IdentifierRequired = "Enter an exchange code or an ISIN so the instrument can be told apart.";
    internal const string NameRequired = "Give the instrument a name.";
    internal const string CurrencyRequired = "Choose the currency the instrument is quoted in.";
    internal const string DuplicateMessage = "This instrument already exists.";
    internal const string LockedLine = "Locked because this instrument has prices or sources.";

    /// <summary>The <c>409</c> code for a listing that already exists (#446 §9). A stable wire value.</summary>
    internal const string DuplicateCode = "instrument-duplicate";

    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>When set, the dialog edits this instrument. Null is create mode.</summary>
    [Parameter] public ExistingInstrument? Instrument { get; set; }

    /// <summary>Whether to offer <i>Go to it</i> on a duplicate (<c>instruments.read</c>).</summary>
    [Parameter] public bool CanGoTo { get; set; }

    /// <summary>The row menu trigger's host, when opened from a menu.</summary>
    [Parameter] public string? ReturnFocusToId { get; set; }

    [Parameter] public EventCallback<ExistingInstrument> OnCreated { get; set; }

    [Parameter] public EventCallback<ExistingInstrument> OnUpdated { get; set; }

    /// <summary>Raised with the existing instrument's id from <i>Go to it</i>; the dialog closes first.</summary>
    [Parameter] public EventCallback<Guid> OnGoTo { get; set; }

    private static readonly Regex Symbol = new(InstrumentIdentifierRules.SymbolPattern, RegexOptions.CultureInvariant);
    private static readonly Regex Mic = new(InstrumentIdentifierRules.MicPattern, RegexOptions.CultureInvariant);
    private static readonly Regex Isin = new(InstrumentIdentifierRules.IsinPattern, RegexOptions.CultureInvariant);

    private static readonly string[] FieldOrder = ["type", "symbol", "micCode", "isin", "name", "currencyCode"];

    private static readonly Dictionary<string, string> FieldLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["type"] = "Type", ["symbol"] = "Symbol", ["micCode"] = "Exchange code", ["isin"] = "ISIN",
        ["name"] = "Name", ["currencyCode"] = "Currency",
    };

    private readonly string _uid = Guid.NewGuid().ToString("N")[..8];
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private InstrumentType _type = InstrumentType.Stock;
    private string? _symbol;
    private string? _mic;
    private string? _isin;
    private string? _name;
    private string? _currency;
    private bool _showOnDashboard = true;
    private IReadOnlyList<OdsOption> _currencyOptions = [];
    private bool _duplicate;
    private Guid? _existingId;
    private bool _focusSummary;

    private bool IsEdit => Instrument is not null;

    private bool Locked => Instrument is { IdentityLocked: true };

    private string SummaryId => $"ins-errsum-{_uid}";

    private string FieldId(string key) => $"ins-{_uid}-{key}";

    /// <summary>Which identifiers a type requires (#446 §8): an exchange for a stock or ETF, an ISIN for a fund.</summary>
    internal static bool RequiresMic(InstrumentType type) => type is InstrumentType.Stock or InstrumentType.Etf;

    internal static bool RequiresIsin(InstrumentType type) => type == InstrumentType.Fund;

    private IReadOnlyList<OdsCardSelectOption> TypeOptions =>
    [
        .. Enum.GetValues<InstrumentType>()
            .Where(t => t != InstrumentType.Other || _type == InstrumentType.Other)
            .Select(t =>
            {
                var type = InstrumentVisuals.TypeOf(t);
                return new OdsCardSelectOption
                {
                    Value = t.ToString(), Label = type.Label, Icon = type.Icon, Color = type.Color, Soft = type.Soft,
                };
            }),
    ];

    private static readonly Dictionary<string, object> ReadOnlyAttributes = new()
    {
        ["readonly"] = true,
        ["aria-readonly"] = "true",
    };

    private Dictionary<string, object> LockAttributes => Locked ? ReadOnlyAttributes : [];

    private string? Err(string key) => _errors.GetValueOrDefault(key);

    private IReadOnlyList<KeyValuePair<string, string>> SummaryErrors =>
    [
        .. FieldOrder.Where(_errors.ContainsKey)
            .Select(k => new KeyValuePair<string, string>(k, $"{FieldLabels[k]}: {_errors[k]}")),
    ];

    private IReadOnlyDictionary<string, string> SummaryLinks =>
        FieldOrder.ToDictionary(k => k, k => k == "type" ? FieldId("type-label") : FieldId(k));

    protected override async Task OnInitializedAsync()
    {
        if (Instrument is { } i)
        {
            _type = i.Type;
            _symbol = i.Symbol;
            _mic = i.MicCode;
            _isin = i.Isin;
            _name = i.Name;
            _currency = i.CurrencyCode;
            _showOnDashboard = i.ShowOnDashboard;
        }

        if (Locked)
            return;

        _currencyOptions = await ReferenceData.CurrencyOptionsAsync();
        if (string.IsNullOrEmpty(_currency) && _currencyOptions.Count > 0)
            _currency = _currencyOptions[0].Value;
    }

    private void Set(string key, Action assign)
    {
        assign();
        _errors.Remove(key);
        _duplicate = false;
    }

    private void OnTypeChanged(string value)
    {
        if (Enum.TryParse<InstrumentType>(value, out var type))
            _type = type;
        _errors.Remove("micCode");
        _errors.Remove("isin");
        _errors.Remove("type");
    }

    private void OnCurrencyChanged(string value)
    {
        _currency = value;
        _errors.Remove("currencyCode");
    }

    /// <summary>
    /// The client half of #446 §8, through the shared rules. Returns the errors keyed by the request's JSON names; a
    /// locked identity is the stored one and is not re-checked.
    /// </summary>
    internal static Dictionary<string, string> Validate(
        InstrumentType type, string? symbol, string? mic, string? isin, string? name, string? currency, bool locked)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(name))
            errors["name"] = NameRequired;
        if (locked)
            return errors;

        var normalizedSymbol = InstrumentIdentifierRules.Normalize(symbol);
        if (normalizedSymbol is null)
            errors["symbol"] = SymbolRequired;
        else if (normalizedSymbol.Length > InstrumentIdentifierRules.SymbolMaxLength || !Symbol.IsMatch(normalizedSymbol))
            errors["symbol"] = SymbolFormat;

        var normalizedMic = InstrumentIdentifierRules.Normalize(mic);
        if (normalizedMic is null ? RequiresMic(type) : !Mic.IsMatch(normalizedMic))
            errors["micCode"] = MicFormat;

        var normalizedIsin = InstrumentIdentifierRules.Normalize(isin);
        if (normalizedIsin is null)
        {
            if (RequiresIsin(type))
                errors["isin"] = IsinRequired;
        }
        else if (!Isin.IsMatch(normalizedIsin))
            errors["isin"] = IsinFormat;
        else if (!InstrumentIdentifierRules.IsValidIsinCheckDigit(normalizedIsin))
            errors["isin"] = IsinCheckDigit;

        if (normalizedMic is null && normalizedIsin is null && !errors.ContainsKey("micCode") && !errors.ContainsKey("isin"))
            errors["micCode"] = IdentifierRequired;

        if (string.IsNullOrWhiteSpace(currency))
            errors["currencyCode"] = CurrencyRequired;

        return errors;
    }

    /// <summary>The <c>PUT</c> body for an instrument as it stands — a full replacement, so every field is carried.</summary>
    internal static NewInstrument ToRequest(ExistingInstrument i) => new()
    {
        Symbol = i.Symbol,
        MicCode = i.MicCode,
        Isin = i.Isin,
        Name = i.Name,
        Type = i.Type,
        CurrencyCode = i.CurrencyCode,
        ShowOnDashboard = i.ShowOnDashboard,
    };

    private NewInstrument BuildRequest() => Locked
        ? ToRequest(Instrument!) with { Name = _name!.Trim(), ShowOnDashboard = _showOnDashboard }
        : new NewInstrument
        {
            // Symbol, exchange and ISIN are saved in capital letters (§6.8).
            Symbol = InstrumentIdentifierRules.Normalize(_symbol) ?? string.Empty,
            MicCode = InstrumentIdentifierRules.Normalize(_mic),
            Isin = InstrumentIdentifierRules.Normalize(_isin),
            Name = _name!.Trim(),
            Type = _type,
            CurrencyCode = _currency!.Trim().ToUpperInvariant(),
            ShowOnDashboard = _showOnDashboard,
        };

    private async Task<bool> SaveAsync()
    {
        _duplicate = false;
        _existingId = null;
        _errors.Clear();
        foreach (var (key, message) in Validate(_type, _symbol, _mic, _isin, _name, _currency, Locked))
            _errors[key] = message;

        if (_errors.Count > 0)
        {
            // The submit is the form dialog's event, not this component's: re-render so the summary takes focus.
            _focusSummary = true;
            StateHasChanged();
            return false;
        }

        var request = BuildRequest();
        var result = IsEdit
            ? await Instruments.UpdateAsync(Instrument!.InstrumentId, request)
            : await Instruments.CreateAsync(request);

        if (result.IsSuccess && result.Value is { } saved)
        {
            await (IsEdit ? OnUpdated : OnCreated).InvokeAsync(saved);
            return true;
        }

        if (string.Equals(result.Problem?.Code, DuplicateCode, StringComparison.Ordinal))
        {
            _duplicate = true;
            _existingId = result.Problem!.TryGetExtensionValue<Guid>("existingInstrumentId", out var existing) ? existing : null;
            _errors["symbol"] = DuplicateMessage;
        }
        else
        {
            var outcome = result.IsSuccess ? ApiResult.Success(result.Status) : ApiResult.Failure(result.Status, result.Problem!);
            var lead = result.Status == HttpStatusCode.Forbidden
                ? "You don't have permission to save instruments"
                : IsEdit ? "Unable to save the instrument" : "Unable to create the instrument";
            outcome.ToastOrFields(Snackbar, lead, AssignServerError);
        }

        _focusSummary = SummaryErrors.Count > 0;
        StateHasChanged();
        return false;
    }

    /// <summary>Places a server field error on its control; keys may arrive PascalCase (<c>MicCode</c>) or camelCase.</summary>
    private bool AssignServerError(string field, string message)
    {
        var leaf = field[(field.LastIndexOf('.') + 1)..];
        var key = FieldOrder.FirstOrDefault(k => string.Equals(k, leaf, StringComparison.OrdinalIgnoreCase));
        if (key is null)
            return false;
        _errors[key] = message;
        return true;
    }

    private async Task GoToAsync(Guid id)
    {
        await OpenChanged.InvokeAsync(false);
        await OnGoTo.InvokeAsync(id);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_focusSummary)
            return;
        _focusSummary = false;
        try { await Js.InvokeVoidAsync("odsFocusById", SummaryId); }
        catch (JSException) { }
        catch (InvalidOperationException) { /* no JS runtime under bUnit */ }
    }
}
