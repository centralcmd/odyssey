using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class AddTermDialog
{
    [Parameter, EditorRequired] public ExistingAccount Account { get; set; } = default!;

    /// <summary>The term being edited, or <c>null</c> to create a new one.</summary>
    [Parameter] public ExistingAccountTerm? Term { get; set; }

    /// <summary>The account's existing terms — for the client-side (kind, effectiveFrom) duplicate guard.</summary>
    [Parameter] public IReadOnlyList<ExistingAccountTerm> Existing { get; set; } = [];

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful create/update so the host can reload.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    private bool IsEdit => Term is not null;
    private bool IsRate => TermKindVisuals.Info(_kind).Group == TermGroup.Rate;
    private bool IsPercentage => _unit == TermValueUnit.Percentage;

    private TermKind _kind;
    private TermValueUnit _unit;
    private string _valueStr = "";
    private string _currency = "USD";
    private string _billingPeriod = "";
    private DateTime? _effectiveFrom = DateTime.UtcNow.Date;
    private string? _note = "";
    private bool _isSaving;

    private static readonly IReadOnlyList<OdsSegmentedOption> _unitOptions =
    [
        new() { Value = nameof(TermValueUnit.Percentage), Label = "Percentage", Icon = "percent" },
        new() { Value = nameof(TermValueUnit.Amount), Label = "Amount", Icon = "payments" },
    ];

    private IReadOnlyList<TermKind> _eligibleKinds = [];
    private List<OdsOption> _currencyOptions = [];
    private List<OdsOption> _billingOptions = [];
    private readonly Dictionary<string, string> _errors = new();

    // Sensible default billing period per fee kind (mirrors the design-system dialog).
    private static readonly Dictionary<TermKind, BillingPeriod> DefaultBilling = new()
    {
        [TermKind.ManagementFee] = BillingPeriod.Annually,
        [TermKind.ServiceFee] = BillingPeriod.Monthly,
        [TermKind.TransactionFee] = BillingPeriod.PerTransaction,
        [TermKind.OtherFee] = BillingPeriod.OneTime,
    };

    protected override void OnInitialized()
    {
        _eligibleKinds = TermKindVisuals.EligibleKinds(Account.AccountType);

        _billingOptions =
        [
            new OdsOption("", "Not specified"),
            .. TermKindVisuals.BillingPeriods.Select(b => new OdsOption(b.ToString(), TermKindVisuals.BillingInfo(b)!.Label)),
        ];

        if (Term is not null)
        {
            _kind = Term.TermKind;
            _unit = Term.ValueUnit;
            _valueStr = Term.ValueUnit == TermValueUnit.Percentage ? FractionToPercentString(Term.Value) : Term.Value.ToString(CultureInfo.InvariantCulture);
            _currency = Term.CurrencyCode ?? Account.CurrencyCode;
            _billingPeriod = Term.BillingPeriod?.ToString() ?? "";
            _effectiveFrom = Term.EffectiveFrom.Date;
            _note = Term.Note ?? "";
        }
        else
        {
            _kind = _eligibleKinds.Count > 0 ? _eligibleKinds[0] : TermKind.OtherFee;
            _unit = TermKindVisuals.Info(_kind).DefaultUnit;
            _currency = Account.CurrencyCode;
            _billingPeriod = DefaultBillingFor(_kind);
            _effectiveFrom = DateTime.UtcNow.Date;
        }
    }

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        var currencies = await ReferenceData.CurrenciesAsync();
        if (currencies.Count > 0)
        {
            _currencyOptions = currencies
                .Where(c => c.Archived is null)
                .OrderBy(c => c.CurrencyCode)
                // The label is the currency NAME alone — OdsMoneyField renders the ISO code itself,
                // in mono, so a "USD · US Dollar" label would print the code twice.
                .Select(c => new OdsOption(c.CurrencyCode, c.Name))
                .ToList();
        }

        // Guarantee the account's own currency is selectable even if the list failed to load.
        if (!_currencyOptions.Any(o => o.Value == _currency))
            _currencyOptions.Insert(0, new OdsOption(_currency, _currency));

        StateHasChanged();
    }

    private string DefaultBillingFor(TermKind kind) =>
        DefaultBilling.TryGetValue(kind, out var b) ? b.ToString() : "";

    private void PickKind(TermKind kind)
    {
        _kind = kind;
        var info = TermKindVisuals.Info(kind);
        _unit = info.DefaultUnit;
        _billingPeriod = info.Group == TermGroup.Fee ? DefaultBillingFor(kind) : "";
        _errors.Clear();
    }

    private void OnUnitChanged(string value)
    {
        if (Enum.TryParse<TermValueUnit>(value, out var unit))
            _unit = unit;
        _errors.Remove("value");
    }

    private void OnValueChanged(string value)
    {
        _valueStr = value;
        _errors.Remove("value");
    }

    private void OnCurrencyChanged(string value) => _currency = value;
    private void OnBillingChanged(string value) => _billingPeriod = value;
    private void OnNoteChanged(string value) => _note = value;

    private void OnEffectiveFromChanged(DateTime? date)
    {
        _effectiveFrom = date;
        _errors.Remove("effectiveFrom");
    }

    private string PreviewFraction
    {
        get
        {
            var raw = ParseValue();
            return raw is null ? "—" : (raw.Value / 100m).ToString("0.0000", CultureInfo.InvariantCulture);
        }
    }

    private string BillingSuffixHint
    {
        get
        {
            if (string.IsNullOrEmpty(_billingPeriod) || !Enum.TryParse<BillingPeriod>(_billingPeriod, out var b) || b == BillingPeriod.OneTime)
                return "";
            return $" · {TermKindVisuals.BillingInfo(b)!.Label}";
        }
    }

    private decimal? ParseValue() =>
        decimal.TryParse(_valueStr.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private async Task CloseAsync() => await OpenChanged.InvokeAsync(false);

    private async Task SubmitAsync()
    {
        if (_isSaving)
            return;

        _errors.Clear();

        if (!TermKindVisuals.IsEligible(_kind, Account.AccountType))
            _errors["kind"] = "Not available for this account type.";

        var raw = ParseValue();
        if (raw is null)
        {
            _errors["value"] = "Enter a value.";
        }
        else if (IsPercentage)
        {
            if (raw < -100m || raw > 100m)
                _errors["value"] = "Rate must be between −100% and 100%.";
        }
        else if (raw < 0m)
        {
            _errors["value"] = "A fee amount can’t be negative.";
        }

        if (_effectiveFrom is null)
            _errors["effectiveFrom"] = "Pick the date this takes effect.";

        if ((_note?.Length ?? 0) > 512)
            _errors["note"] = "Keep the note under 512 characters.";

        // Duplicate (kind, effectiveFrom) → the server's 409, excluding the row being edited.
        if (_effectiveFrom is { } date && Existing.Any(t =>
                t.AccountTermId != (Term?.AccountTermId ?? Guid.Empty)
                && t.TermKind == _kind
                && t.EffectiveFrom.Date == date.Date))
        {
            _errors["effectiveFrom"] = "This kind already has an entry on that date.";
        }

        if (_errors.Count > 0)
            return;

        var value = IsPercentage
            ? Math.Round(raw!.Value / 100m, 6)
            : Math.Round(raw!.Value, 2);

        var dto = new NewAccountTerm
        {
            TermKind = _kind,
            ValueUnit = _unit,
            Value = value,
            CurrencyCode = IsPercentage ? null : _currency,
            BillingPeriod = IsRate || string.IsNullOrEmpty(_billingPeriod)
                ? null
                : Enum.Parse<BillingPeriod>(_billingPeriod),
            EffectiveFrom = DateTime.SpecifyKind(_effectiveFrom!.Value.Date, DateTimeKind.Utc),
            Note = string.IsNullOrWhiteSpace(_note) ? null : _note!.Trim(),
        };

        _isSaving = true;
        try
        {
            var ok = IsEdit
                ? (await Accounts.UpdateTermAsync(Account.AccountId, Term!.AccountTermId, dto))
                    .Toast(Snackbar, "Unable to update term", "Term updated.")
                : (await Accounts.AddTermAsync(Account.AccountId, dto))
                    .Toast(Snackbar, "Unable to create term", "Term created.");

            if (!ok)
                return;

            await OnSaved.InvokeAsync();
            await OpenChanged.InvokeAsync(false);
        }
        finally
        {
            _isSaving = false;
        }
    }

    private static string FractionToPercentString(decimal fraction)
    {
        var p = fraction * 100m;
        return p.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
