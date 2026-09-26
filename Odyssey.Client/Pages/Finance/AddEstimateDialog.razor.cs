using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class AddEstimateDialog
{
    // One dialog, two owners (the design system's AddEstimateModal takes `ownerNoun` for the same
    // reason): an account or a property. Exactly one of Account / Property is set by the host; the
    // estimate rules are the same on both, so a second dialog would only be a second place for the
    // validation copy to drift.

    /// <summary>The account the estimate belongs to. Set this or <see cref="Property"/>, not both.</summary>
    [Parameter] public ExistingAccount? Account { get; set; }

    /// <summary>The estimate being edited, or <c>null</c> to create a new one.</summary>
    [Parameter] public ExistingAccountEstimate? Estimate { get; set; }

    /// <summary>The account's existing estimates — for the client-side EffectiveFrom duplicate guard.</summary>
    [Parameter] public IReadOnlyList<ExistingAccountEstimate> Existing { get; set; } = [];

    /// <summary>The property the estimate belongs to (issue #167). Set this or <see cref="Account"/>.</summary>
    [Parameter] public ExistingProperty? Property { get; set; }

    /// <summary>The property estimate being edited, or <c>null</c> to create a new one.</summary>
    [Parameter] public ExistingPropertyEstimate? PropertyEstimate { get; set; }

    /// <summary>The property's existing estimates — for the same duplicate guard.</summary>
    [Parameter] public IReadOnlyList<ExistingPropertyEstimate> ExistingPropertyEstimates { get; set; } = [];

    private bool ForProperty => Property is not null;

    private string OwnerName => ForProperty ? Property!.Name : Account!.Name;

    private string OwnerCurrency => ForProperty ? Property!.CurrencyCode : Account!.CurrencyCode;

    private string OwnerNoun => ForProperty ? "property" : "account";

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful create/update so the host can reload.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    private bool IsEdit => ForProperty ? PropertyEstimate is not null : Estimate is not null;

    // The account-type hint says whether an estimate suits the account's type. A property's type IS
    // the reason it has estimates, so the hint has nothing to add there (the DS `showHint={false}`).
    private bool ShowHint => !ForProperty && !IsEdit;

    private string _valueStr = "";
    private DateTime? _effectiveFrom = DateTime.UtcNow.Date;
    private string? _note = "";
    private bool _isSaving;
    private bool _recommended;
    // The account currency's own decimals (JPY renders none). The CODE is never resolved here — it
    // is Account.CurrencyCode and always known; only the decimals need the reference-data round trip.
    private int _minorUnits = OdsMoney.DefaultMinorUnits;

    private readonly Dictionary<string, string> _errors = new();

    protected override void OnInitialized()
    {
        _recommended = Account is not null && EstimateVisuals.IsRecommended(Account.AccountType);

        if (ForProperty && PropertyEstimate is { } pe)
            Seed(pe.Value, pe.EffectiveFrom, pe.Note);
        else if (!ForProperty && Estimate is { } ae)
            Seed(ae.Value, ae.EffectiveFrom, ae.Note);
        else
        {
            _effectiveFrom = DateTime.UtcNow.Date;
        }
    }

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        var currencies = await ReferenceData.CurrenciesAsync();
        var currency = currencies
            .FirstOrDefault(c => string.Equals(c.CurrencyCode, OwnerCurrency, StringComparison.OrdinalIgnoreCase));
        if (currency is not null)
            _minorUnits = OdsMoney.MinorUnitsOf(currency);

        StateHasChanged();
    }

    private void Seed(decimal value, DateTime effectiveFrom, string? note)
    {
        _valueStr = value.ToString(CultureInfo.InvariantCulture);
        _effectiveFrom = effectiveFrom.Date;
        _note = note ?? "";
    }

    private void OnValueChanged(string value)
    {
        _valueStr = value;
        _errors.Remove("value");
    }

    private void OnNoteChanged(string value) => _note = value;

    private void OnEffectiveFromChanged(DateTime? date)
    {
        _effectiveFrom = date;
        _errors.Remove("effectiveFrom");
    }

    private decimal? ParseValue() =>
        OdsMoneyText.Parse(_valueStr);

    private string? Preview
    {
        get
        {
            var raw = ParseValue();
            return raw is null ? null : OdsMoney.Format(raw, OwnerCurrency, _minorUnits);
        }
    }

    private async Task CloseAsync() => await OpenChanged.InvokeAsync(false);

    private async Task SubmitAsync()
    {
        if (_isSaving)
            return;

        _errors.Clear();

        var raw = ParseValue();
        if (raw is null)
            _errors["value"] = "Enter an estimated value.";
        else if (raw < 0m)
            _errors["value"] = "An estimate can’t be negative.";

        if (_effectiveFrom is null)
            _errors["effectiveFrom"] = "Pick the date this takes effect.";

        if ((_note?.Length ?? 0) > 512)
            _errors["note"] = "Keep the note under 512 characters.";

        // Duplicate EffectiveFrom → the server's 409, excluding the row being edited.
        if (_effectiveFrom is { } date && IsDuplicateDate(date))
            _errors["effectiveFrom"] = $"This {OwnerNoun} already has an estimate on that date.";

        if (_errors.Count > 0)
            return;

        var value = Math.Round(raw!.Value, 2);
        var effectiveFrom = DateTime.SpecifyKind(_effectiveFrom!.Value.Date, DateTimeKind.Utc);
        var trimmedNote = string.IsNullOrWhiteSpace(_note) ? null : _note!.Trim();

        _isSaving = true;
        try
        {
            var ok = ForProperty
                ? await SavePropertyEstimateAsync(value, effectiveFrom, trimmedNote)
                : await SaveAccountEstimateAsync(value, effectiveFrom, trimmedNote);

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

    private bool IsDuplicateDate(DateTime date) => ForProperty
        ? ExistingPropertyEstimates.Any(e =>
            e.PropertyEstimateId != (PropertyEstimate?.PropertyEstimateId ?? Guid.Empty)
            && e.EffectiveFrom.Date == date.Date)
        : Existing.Any(e =>
            e.AccountEstimateId != (Estimate?.AccountEstimateId ?? Guid.Empty)
            && e.EffectiveFrom.Date == date.Date);

    private async Task<bool> SaveAccountEstimateAsync(decimal value, DateTime effectiveFrom, string? note)
    {
        var dto = new NewAccountEstimate
        {
            Value = value,
            CurrencyCode = Account!.CurrencyCode,
            EffectiveFrom = effectiveFrom,
            Note = note,
        };

        return IsEdit
            ? (await Accounts.UpdateEstimateAsync(Account.AccountId, Estimate!.AccountEstimateId, dto))
                .Toast(Snackbar, "Unable to update estimate", "Estimate updated.")
            : (await Accounts.AddEstimateAsync(Account.AccountId, dto))
                .Toast(Snackbar, "Unable to create estimate", "Estimate created.");
    }

    private async Task<bool> SavePropertyEstimateAsync(decimal value, DateTime effectiveFrom, string? note)
    {
        var dto = new NewPropertyEstimate
        {
            Value = value,
            CurrencyCode = Property!.CurrencyCode,
            EffectiveFrom = effectiveFrom,
            Note = note,
        };

        return IsEdit
            ? (await Properties.UpdateEstimateAsync(Property.PropertyId, PropertyEstimate!.PropertyEstimateId, dto))
                .Toast(Snackbar, "Unable to update estimate", "Estimate updated.")
            : (await Properties.AddEstimateAsync(Property.PropertyId, dto))
                .Toast(Snackbar, "Unable to create estimate", "Estimate created.");
    }
}
