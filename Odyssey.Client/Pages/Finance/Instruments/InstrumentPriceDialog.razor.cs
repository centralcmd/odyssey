using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance.Instruments;

public partial class InstrumentPriceDialog
{
    /// <summary>The <c>409</c> code of a second manual price for one instrument and day (#446 §9).</summary>
    internal const string PriceDuplicateCode = "price-duplicate";

    /// <summary>The <c>409</c> extension naming the manual price that already holds the day.</summary>
    internal const string ExistingPriceIdExtension = "existingInstrumentPriceId";

    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Adds a price for this instrument only: the instrument is a read-only field (Add price from an instrument).</summary>
    [Parameter] public Guid? LockedInstrumentId { get; set; }

    /// <summary>The manual price being edited; null adds a new one.</summary>
    [Parameter] public ExistingInstrumentPrice? Price { get; set; }

    /// <summary>Open with focus on Close rather than the first field — the "Edit that price" hand-off (§3.3).</summary>
    [Parameter] public bool FocusClose { get; set; }

    /// <summary>Where focus returns on close (a row menu's trigger).</summary>
    [Parameter] public string? ReturnFocusToId { get; set; }

    /// <summary>Raised with the saved price, before the dialog closes.</summary>
    [Parameter] public EventCallback<ExistingInstrumentPrice> OnSaved { get; set; }

    /// <summary>Raised with the existing price's id when the user picks "Edit that price" after a duplicate.</summary>
    [Parameter] public EventCallback<Guid> OnEditExisting { get; set; }

    /// <summary>True while an edit's <c>PUT</c> is in flight, false once it returns — the host's <c>RowBusy</c>.</summary>
    [Parameter] public EventCallback<bool> OnBusyChanged { get; set; }

    private readonly string _uid = Guid.NewGuid().ToString("N")[..8];
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExistingInstrument> _known = [];

    private string? _instrumentValue;
    private ExistingInstrument? _locked;
    private bool _lockedMissing;
    private DateTime? _tradingDate;
    private string _close = string.Empty;
    private DateOnly? _today;
    private bool _canUpdate;
    private bool _duplicate;
    private Guid? _duplicateId;
    private bool _focusSummary;
    private bool _focusClose;

    private bool IsEdit => Price is not null;

    private string SummaryId => $"ipd-{_uid}-summary";
    private string InstrumentFieldId => $"ipd-{_uid}-instrument";
    private string DateFieldId => $"ipd-{_uid}-date";
    private string CloseFieldId => $"ipd-{_uid}-close";

    private static string CloseHelp => $"Up to {InstrumentPriceRules.MaxDecimals} decimals";

    private static string DecimalsMessage => $"Use at most {InstrumentPriceRules.MaxDecimals} decimals.";

    internal const string FutureDateMessage = "Prices can only be entered for today or earlier.";

    internal const string PriceAboveZeroMessage = "Enter a price above 0.";

    // The future-date bound is Oslo's today; with no resolvable zone the field is unbounded and the server's 422
    // remains (#446 v6 §8.7).
    private DateTime? MaxDate => _today?.ToDateTime(TimeOnly.MinValue);

    private string DateHelp => _today is null ? "Prices can only be entered for today or earlier." : "No future dates.";

    /// <summary>The instrument shown read-only (editing, or locked to one), as "SYMBOL · Name".</summary>
    private string? FixedLabel =>
        Price is { } price ? Label(price.Symbol, price.InstrumentName)
        : _locked is { } locked ? Label(locked.Symbol, locked.Name)
        : LockedInstrumentId is not null ? "Loading…"
        : null;

    private string? CurrencyCode =>
        Price?.CurrencyCode
        ?? _locked?.CurrencyCode
        ?? (_instrumentValue is { } id && _known.TryGetValue(id, out var picked) ? picked.CurrencyCode : null);

    private IReadOnlyList<OdsOption> InstrumentOptions =>
        _instrumentValue is { } id && _known.TryGetValue(id, out var picked) ? [OptionFor(picked)] : [];

    private IReadOnlyList<KeyValuePair<string, string>> SummaryErrors =>
    [
        .. new[] { "instrument", "date", "close" }
            .Where(_errors.ContainsKey)
            .Select(k => new KeyValuePair<string, string>(k, $"{FieldLabel(k)} — {_errors[k]}")),
    ];

    private IReadOnlyDictionary<string, string> SummaryLinks => new Dictionary<string, string>
    {
        ["instrument"] = InstrumentFieldId,
        ["date"] = DateFieldId,
        ["close"] = CloseFieldId,
    };

    private static string FieldLabel(string key) => key switch
    {
        "instrument" => "Instrument",
        "date" => "Trading date",
        _ => "Close",
    };

    private static string Label(string symbol, string name) => $"{symbol} · {name}";

    private static OdsOption OptionFor(ExistingInstrument instrument) => InstrumentVisuals.OptionFor(instrument);

    private string? Err(string key) => _errors.TryGetValue(key, out var message) ? message : null;

    protected override async Task OnInitializedAsync()
    {
        _today = InstrumentVisuals.TryOsloToday(Time);
        _focusClose = FocusClose;

        if (Price is { } price)
        {
            _tradingDate = price.TradingDate.ToDateTime(TimeOnly.MinValue);
            _close = price.Close.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            _tradingDate = _today?.ToDateTime(TimeOnly.MinValue);
        }

        var user = await AuthenticationStateProvider.GetUserAsync();
        _canUpdate = user.HasPermission(PermissionClaims.InstrumentPricesUpdate);

        if (Price is null && LockedInstrumentId is { } lockedId)
        {
            var result = await Instruments.GetAsync(lockedId);
            if (result is { IsSuccess: true, Value: { } instrument })
                _locked = instrument;
            else
                _lockedMissing = true;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var target = _focusSummary ? SummaryId : _focusClose ? CloseFieldId : null;
        _focusSummary = false;
        _focusClose = false;
        if (target is null)
            return;

        try { await Js.InvokeVoidAsync("odsFocusById", target); }
        catch (JSException) { }
        catch (InvalidOperationException) { /* no JS runtime (bUnit) */ }
    }

    private async Task<IReadOnlyList<OdsOption>> SearchInstrumentsAsync(string query, CancellationToken ct)
    {
        // Archived instruments are excluded by the endpoint unless asked for; a price cannot be added to one.
        var result = await Instruments.ListAsync(1, 20, new InstrumentsFilter { Search = query, IncludeArchived = false }, ct);
        if (!result.IsSuccess)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(result.Error);
        }

        var items = result.Value?.Items ?? [];
        foreach (var instrument in items)
            _known[instrument.InstrumentId.ToString()] = instrument;
        return [.. items.Select(OptionFor)];
    }

    private async Task<OdsOption?> ResolveInstrumentAsync(string value)
    {
        if (_known.TryGetValue(value, out var known))
            return OptionFor(known);
        if (!Guid.TryParse(value, out var id))
            return null;

        var result = await Instruments.GetAsync(id);
        if (result is not { IsSuccess: true, Value: { } instrument })
            return null;
        _known[value] = instrument;
        return OptionFor(instrument);
    }

    private void OnInstrumentChanged(string? value)
    {
        _instrumentValue = string.IsNullOrEmpty(value) ? null : value;
        ClearError("instrument");
    }

    private void OnDateChanged(DateTime? value)
    {
        _tradingDate = value;
        ClearError("date");
    }

    private void OnCloseChanged(string value)
    {
        _close = value ?? string.Empty;
        ClearError("close");
    }

    private void ClearError(string key)
    {
        _errors.Remove(key);
        if (key == "date")
        {
            _duplicate = false;
            _duplicateId = null;
        }
    }

    /// <summary>
    /// The client half of #446 §8: the same shared rules the server runs. Value-based decimals (trailing zeros
    /// ignored), and the future-date check only while Oslo's today is known.
    /// </summary>
    internal static Dictionary<string, string> Validate(Guid? instrumentId, DateOnly? tradingDate, string closeText, DateOnly? today)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (instrumentId is null)
            errors["instrument"] = "Choose an instrument.";

        if (tradingDate is not { } date)
            errors["date"] = "Enter the trading date.";
        else if (today is { } now && date > now)
            errors["date"] = FutureDateMessage;

        var close = OdsMoneyText.Parse(closeText);
        if (close is not { } value || value < Min || value > Max)
            errors["close"] = PriceAboveZeroMessage;
        else if (!InstrumentPriceRules.HasAtMostDecimals(value))
            errors["close"] = DecimalsMessage;

        return errors;
    }

    private static readonly decimal Min = decimal.Parse(Odyssey.Dtos.MoneyBounds.InstrumentPriceMin, System.Globalization.CultureInfo.InvariantCulture);
    private static readonly decimal Max = decimal.Parse(Odyssey.Dtos.MoneyBounds.InstrumentPriceMax, System.Globalization.CultureInfo.InvariantCulture);

    private Guid? SelectedInstrumentId =>
        Price?.InstrumentId ?? _locked?.InstrumentId
        ?? (Guid.TryParse(_instrumentValue, out var id) ? id : null);

    private async Task<bool> SaveAsync()
    {
        _duplicate = false;
        _duplicateId = null;
        _errors.Clear();

        var tradingDate = _tradingDate is { } picked ? DateOnly.FromDateTime(picked) : (DateOnly?)null;
        foreach (var (key, message) in Validate(SelectedInstrumentId, tradingDate, _close, _today))
            _errors[key] = message;

        if (_errors.Count > 0)
        {
            // The submit is the form dialog's event, not this component's: re-render so the summary takes focus.
            _focusSummary = true;
            StateHasChanged();
            return false;
        }

        var close = OdsMoneyText.Parse(_close)!.Value;
        ApiResult<ExistingInstrumentPrice> result;
        if (Price is { } existing)
        {
            await OnBusyChanged.InvokeAsync(true);
            try
            {
                result = await Prices.UpdateAsync(existing.InstrumentPriceId, new UpdateInstrumentPrice { TradingDate = tradingDate, Close = close });
            }
            finally
            {
                await OnBusyChanged.InvokeAsync(false);
            }
        }
        else
        {
            result = await Prices.CreateAsync(new NewInstrumentPrice { InstrumentId = SelectedInstrumentId, TradingDate = tradingDate, Close = close });
        }

        if (result is { IsSuccess: true, Value: { } saved })
        {
            Snackbar.Add(IsEdit ? "Price saved." : "Price created.", MudBlazor.Severity.Success);
            await OnSaved.InvokeAsync(saved);
            return true;
        }

        var outcome = ApiResult.Failure(result.Status, result.Problem ?? new ApiProblem { Detail = result.Error });
        if (result.Status == HttpStatusCode.Conflict
            && string.Equals(result.Problem?.Code, PriceDuplicateCode, StringComparison.Ordinal))
        {
            _duplicate = true;
            _duplicateId = result.Problem!.TryGetExtensionValue<Guid>(ExistingPriceIdExtension, out var existingId)
                ? existingId
                : null;
        }

        outcome.ToastOrFields(Snackbar, IsEdit ? "Unable to save the price" : "Unable to create the price", AssignServerError);
        _focusSummary = SummaryErrors.Count > 0;
        StateHasChanged();
        return false;
    }

    /// <summary>Places a server field error (<c>InstrumentId</c>, <c>TradingDate</c>, <c>Close</c>) on its control.</summary>
    private bool AssignServerError(string field, string message)
    {
        var leaf = field[(field.LastIndexOf('.') + 1)..];
        var key = leaf.ToLowerInvariant() switch
        {
            "instrumentid" or "instrument" => "instrument",
            "tradingdate" => "date",
            "close" => "close",
            _ => null,
        };
        if (key is null || (key == "instrument" && FixedLabel is not null))
            return false;

        _errors[key] = message;
        return true;
    }

    private async Task EditExistingAsync()
    {
        if (_duplicateId is not { } id || !_canUpdate)
            return;

        await OnEditExisting.InvokeAsync(id);
    }
}
