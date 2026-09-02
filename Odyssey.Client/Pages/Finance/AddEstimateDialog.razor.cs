using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class AddEstimateDialog
{
    [Parameter, EditorRequired] public ExistingAccount Account { get; set; } = default!;

    /// <summary>The estimate being edited, or <c>null</c> to create a new one.</summary>
    [Parameter] public ExistingAccountEstimate? Estimate { get; set; }

    /// <summary>The account's existing estimates — for the client-side EffectiveFrom duplicate guard.</summary>
    [Parameter] public IReadOnlyList<ExistingAccountEstimate> Existing { get; set; } = [];

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful create/update so the host can reload.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    private bool IsEdit => Estimate is not null;

    private string _valueStr = "";
    private DateTime? _effectiveFrom = DateTime.UtcNow.Date;
    private string? _note = "";
    private bool _isSaving;
    private bool _recommended;
    private string _currencySymbol = "$";

    private readonly Dictionary<string, string> _errors = new();

    protected override void OnInitialized()
    {
        _recommended = EstimateVisuals.IsRecommended(Account.AccountType);
        _currencySymbol = Account.CurrencyCode; // replaced with the real symbol once currencies load

        if (Estimate is not null)
        {
            _valueStr = Estimate.Value.ToString(CultureInfo.InvariantCulture);
            _effectiveFrom = Estimate.EffectiveFrom.Date;
            _note = Estimate.Note ?? "";
        }
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
        var symbol = currencies
            .FirstOrDefault(c => string.Equals(c.CurrencyCode, Account.CurrencyCode, StringComparison.OrdinalIgnoreCase))?.Symbol;
        if (!string.IsNullOrWhiteSpace(symbol))
            _currencySymbol = symbol;

        StateHasChanged();
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
        decimal.TryParse(_valueStr.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private string? Preview
    {
        get
        {
            var raw = ParseValue();
            if (raw is null)
                return null;
            var nf = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
            nf.CurrencySymbol = _currencySymbol;
            nf.CurrencyNegativePattern = 1;
            return raw.Value.ToString("C", nf);
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
        if (_effectiveFrom is { } date && Existing.Any(e =>
                e.AccountEstimateId != (Estimate?.AccountEstimateId ?? Guid.Empty)
                && e.EffectiveFrom.Date == date.Date))
        {
            _errors["effectiveFrom"] = "This account already has an estimate on that date.";
        }

        if (_errors.Count > 0)
            return;

        var dto = new NewAccountEstimate
        {
            Value = Math.Round(raw!.Value, 2),
            CurrencyCode = Account.CurrencyCode,
            EffectiveFrom = DateTime.SpecifyKind(_effectiveFrom!.Value.Date, DateTimeKind.Utc),
            Note = string.IsNullOrWhiteSpace(_note) ? null : _note!.Trim(),
        };

        _isSaving = true;
        try
        {
            var ok = IsEdit
                ? (await Accounts.UpdateEstimateAsync(Account.AccountId, Estimate!.AccountEstimateId, dto))
                    .Toast(Snackbar, "Unable to update estimate", "Estimate updated.")
                : (await Accounts.AddEstimateAsync(Account.AccountId, dto))
                    .Toast(Snackbar, "Unable to create estimate", "Estimate created.");

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
}
