using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class CreateSubscriptionDialog
{
    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Active company options (non-archived contacts).</summary>
    [Parameter] public IReadOnlyList<OdsOption> Companies { get; set; } = [];

    /// <summary>Active currencies (non-archived), supplied by the host so the dialog does not re-fetch on every open.</summary>
    [Parameter] public IReadOnlyList<ExistingCurrency> Currencies { get; set; } = [];

    /// <summary>Raised after a successful create/update so the host can refresh.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    /// <summary>When set, the dialog edits this subscription. Null = create mode.</summary>
    [Parameter] public ExistingSubscription? Subscription { get; set; }

    private bool IsEdit => Subscription is not null;

    private string CompanyFieldId => IsEdit ? $"sub-edit-company-{Subscription!.SubscriptionId}" : "sub-new-company";

    private string? _name;
    private string? _externalId;
    private string? _contactId;
    private DateTime? _startDate = DateTime.UtcNow.Date;
    private DateTime? _endDate;
    private string? _amount;
    private string _currencyCode = "USD";
    private string _interval = string.Empty;
    private int _intervalCount = 1;
    private DateTime? _firstBillingDate = DateTime.UtcNow.Date;
    private string? _notes;

    private void OnIntervalChanged(string value)
    {
        _interval = value;
        _intervalError = null;
    }

    private void OnIntervalCountChanged(decimal? value) =>
        _intervalCount = OdsBillingIntervalChip.NormalizeCount((int)Math.Round(value ?? 1));

    private string EveryHelpText => string.IsNullOrEmpty(_interval)
        ? "Pick a cadence first."
        : OdsBillingIntervalChip.EveryHelp(
            Enum.TryParse<BillingInterval>(_interval, out var iv) ? iv : BillingInterval.Monthly, _intervalCount);

    private string? _nameError;
    private string? _intervalError;
    private string? _startError;
    private string? _endError;
    private string? _amountError;
    private string? _firstBillingError;

    // The option label is the currency NAME alone — OdsMoneyField renders the ISO code itself, in
    // mono, so a "USD · US Dollar" label would print the code twice.
    private IReadOnlyList<OdsOption> CurrencyOptions =>
        [.. Currencies.Where(c => c.Archived is null)
            .OrderBy(c => c.CurrencyCode)
            .Select(c => new OdsOption(c.CurrencyCode, c.Name))];

    protected override void OnInitialized()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        if (Subscription is { } subscription)
        {
            _name = subscription.Name;
            _externalId = subscription.ExternalId;
            _contactId = subscription.Contact?.ContactId.ToString();
            _startDate = subscription.StartDate.ToDateTime(TimeOnly.MinValue);
            _endDate = subscription.EndDate is { } end ? end.ToDateTime(TimeOnly.MinValue) : null;
            _amount = subscription.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _currencyCode = subscription.CurrencyCode;
            _interval = subscription.Interval.ToString();
            _intervalCount = subscription.IntervalCount;
            _firstBillingDate = subscription.FirstBillingDate.ToDateTime(TimeOnly.MinValue);
            _notes = subscription.Notes;
            return;
        }

        _currencyCode = UserPreferences.DefaultCurrency ?? "USD";
        // Fall back to the first supplied currency if the preferred one isn't in the active list.
        if (!string.IsNullOrEmpty(_currencyCode) && Currencies.Count > 0
            && Currencies.All(currency => !string.Equals(currency.CurrencyCode, _currencyCode, StringComparison.OrdinalIgnoreCase)))
        {
            _currencyCode = Currencies[0].CurrencyCode;
        }
    }

    private void OnCurrencyChanged(string value) => _currencyCode = value;

    private async Task<bool> SaveAsync()
    {
        _nameError = string.IsNullOrWhiteSpace(_name) ? "Give the subscription a name." : null;
        _intervalError = string.IsNullOrEmpty(_interval) ? "Pick a billing cadence." : null;
        _startError = _startDate is null ? "Choose the start date." : null;
        _firstBillingError = _firstBillingDate is null ? "Choose the first billing date." : null;
        _endError = _endDate is { } end && _startDate is { } start && end.Date < start.Date
            ? "End date must be on or after the start date."
            : null;

        // NumberStyles.Number allowed thousands separators, so the decimal comma the money editor
        // accepts ("1234,56") was read as a group separator — 123456.
        var parsed = OdsMoneyText.Parse(_amount);
        var amount = parsed ?? 0m;
        _amountError = parsed is null || amount < 0 ? "Enter a valid, non-negative price." : null;

        if (_nameError is not null || _intervalError is not null || _startError is not null
            || _firstBillingError is not null || _endError is not null || _amountError is not null)
            return false;

        if (string.IsNullOrWhiteSpace(_currencyCode))
        {
            Snackbar.Add("Currency is required.", Severity.Error);
            return false;
        }

        var contactId = Guid.TryParse(_contactId, out var cp) ? (Guid?)cp : null;
        var startDate = DateOnly.FromDateTime(_startDate!.Value);
        var endDate = _endDate is { } e ? DateOnly.FromDateTime(e) : (DateOnly?)null;
        var currencyCode = _currencyCode.Trim().ToUpperInvariant();
        var interval = Enum.TryParse<BillingInterval>(_interval, out var iv) ? iv : BillingInterval.Monthly;
        var firstBillingDate = DateOnly.FromDateTime(_firstBillingDate!.Value);
        var notes = string.IsNullOrWhiteSpace(_notes) ? null : _notes!.Trim();

        if (Subscription is { } existing)
        {
            var update = new UpdateSubscription
            {
                Name = _name!.Trim(),
                ExternalId = string.IsNullOrWhiteSpace(_externalId) ? null : _externalId!.Trim(),
                ContactId = contactId,
                StartDate = startDate,
                EndDate = endDate,
                Amount = amount,
                CurrencyCode = currencyCode,
                Interval = interval,
                IntervalCount = _intervalCount,
                FirstBillingDate = firstBillingDate,
                Notes = notes,
                // Pause / archive are separate row actions — preserve whatever the record already has.
                Paused = existing.Paused is not null,
                Archived = existing.Archived is not null,
            };

            return (await Subscriptions.UpdateAsync(existing.SubscriptionId, update)).Toast(Snackbar,
                "Unable to update subscription", "Subscription updated.");
        }

        var subscription = new NewSubscription
        {
            Name = _name!.Trim(),
            ExternalId = string.IsNullOrWhiteSpace(_externalId) ? null : _externalId!.Trim(),
            ContactId = contactId,
            StartDate = startDate,
            EndDate = endDate,
            Amount = amount,
            CurrencyCode = currencyCode,
            Interval = interval,
            IntervalCount = _intervalCount,
            FirstBillingDate = firstBillingDate,
            Notes = notes,
        };

        return (await Subscriptions.CreateAsync(subscription)).Toast(Snackbar, "Unable to create subscription", "Subscription created.");
    }
}
