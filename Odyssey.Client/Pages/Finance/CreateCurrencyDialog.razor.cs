using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using Odyssey.Client.Authorization;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class CreateCurrencyDialog
{
    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>Raised after a successful create/update so the host can refresh.</summary>
    [Parameter] public EventCallback OnSaved { get; set; }

    /// <summary>When set, the dialog edits this currency (code locked). Null = create mode.</summary>
    [Parameter] public ExistingCurrency? Currency { get; set; }

    /// <summary>Whether the caller may set the sync start date (<c>system-settings.update</c>); the host decides.</summary>
    [Parameter] public bool SyncStartEditable { get; set; }

    /// <summary>Raised with the currency code after its sync start date was saved, for the host to announce.</summary>
    [Parameter] public EventCallback<string> OnSyncStartSaved { get; set; }

    /// <summary>The instance's effective default start date, for a new currency's placeholder; null when unknown.</summary>
    [Parameter] public DateOnly? DefaultSyncStart { get; set; }

    private DateTime? _syncStart;
    private string? _syncStartError;

    // Set once a new currency is created, so a resubmit after a refused start date retries only the date
    // instead of creating the currency a second time.
    private string? _createdCode;

    // The base has no start date of its own and an archived currency carries no projection.
    private bool ShowSyncStart =>
        SyncStartEditable && (!IsEdit || Currency!.Sync is { Locked: false });

    private ExistingExchangeRateSyncCurrency? EditedSync => IsEdit ? Currency!.Sync : null;

    private string SyncStartPlaceholder =>
        (EditedSync?.EffectiveHistoryStartDate ?? DefaultSyncStart) is { } effective
            ? $"Default ({Odyssey.Client.Pages.Finance.ExchangeRateSync.ExchangeRateSyncTime.Date(effective)})"
            : "Default";

    private static DateTime HistoryFloor => ExchangeRateHistoryStartDateRule.Floor.ToDateTime(TimeOnly.MinValue);

    private DateTime TodayUtc => Time.GetUtcNow().UtcDateTime.Date;

    private DateOnly? SyncStartDraft => _syncStart is { } date ? DateOnly.FromDateTime(date) : null;

    private string? _currencyCode;
    private string? _name;
    private string? _symbol;
    private int _minorUnits = 2;
    private bool _codeError;
    private bool _nameError;
    private bool _symbolError;

    private bool IsEdit => Currency is not null;

    protected override void OnInitialized()
    {
        if (Currency is null)
            return;

        _currencyCode = Currency.CurrencyCode;
        _name = Currency.Name;
        _symbol = Currency.Symbol;
        _minorUnits = Currency.MinorUnits;
        _syncStart = Currency.Sync?.HistoryStartDate?.ToDateTime(TimeOnly.MinValue);
    }

    private void OnSyncStartChanged(DateTime? value)
    {
        _syncStart = value;
        _syncStartError = null;
    }

    private async Task<bool> SaveAsync()
    {
        _codeError = !IsEdit && string.IsNullOrWhiteSpace(_currencyCode);
        _nameError = string.IsNullOrWhiteSpace(_name);
        _symbolError = string.IsNullOrWhiteSpace(_symbol);
        _syncStartError = ShowSyncStart && _syncStart is { } start && (start.Date < HistoryFloor || start.Date > TodayUtc)
            ? $"Enter a date between {ExchangeRateHistoryStartDateRule.Floor.ToString(ExchangeRateHistoryStartDateRule.Format, CultureInfo.InvariantCulture)} and today."
            : null;
        if (_codeError || _nameError || _symbolError || _syncStartError is not null)
            return false;

        if (IsEdit)
        {
            // Preserve the current archival state — archive/restore is a separate row action.
            var update = new NewCurrency
            {
                CurrencyCode = Currency!.CurrencyCode,
                Name = _name!.Trim(),
                Symbol = _symbol!.Trim(),
                MinorUnits = _minorUnits,
                Archived = Currency.Archived is not null,
            };

            if (!Invalidated((await Currencies.UpdateAsync(Currency.CurrencyCode, update)).Toast(Snackbar, "Update failed", "Currency updated.")))
                return false;

            return await SaveSyncStartAsync(Currency.CurrencyCode, EditedSync?.Enabled ?? false, EditedSync?.HistoryStartDate)
                || await CurrencyWrittenAsync();
        }

        if (_createdCode is null)
        {
            var newCurrency = new NewCurrency
            {
                CurrencyCode = _currencyCode!.Trim().ToUpperInvariant(),
                Name = _name!.Trim(),
                Symbol = _symbol!.Trim(),
                MinorUnits = _minorUnits,
                Archived = false,
            };

            if (!Invalidated((await Currencies.CreateAsync(newCurrency)).Toast(Snackbar, "Unable to create currency", "Currency created.")))
                return false;
            _createdCode = newCurrency.CurrencyCode;
        }

        return await SaveSyncStartAsync(_createdCode, enabled: false, saved: null)
            || await CurrencyWrittenAsync();
    }

    // The currency itself was written but the start date was refused, so the dialog stays open and
    // OdsFormDialog will not raise OnSaved. The host still has to show the written currency, even if the
    // user then cancels.
    private async Task<bool> CurrencyWrittenAsync()
    {
        await OnSaved.InvokeAsync();
        return false;
    }

    // The second write, only when the date differs from what is stored. A refusal keeps the dialog open
    // with the server's message on the field; saving an edit again re-sends the currency's own fields,
    // which is harmless, and a created currency is not created again.
    private async Task<bool> SaveSyncStartAsync(string code, bool enabled, DateOnly? saved)
    {
        if (!ShowSyncStart || SyncStartDraft == saved)
            return true;

        var result = await Sync.UpdateCurrencyAsync(code,
            new ExchangeRateSyncCurrencyUpdate { Enabled = enabled, HistoryStartDate = SyncStartDraft });
        if (result.IsSuccess)
        {
            await OnSyncStartSaved.InvokeAsync(code);
            return true;
        }

        _syncStartError = result.Status == HttpStatusCode.Forbidden
            ? Odyssey.Client.Pages.Finance.ExchangeRateSync.ExchangeRateSyncStatusPanel.PermissionMessage
            : result.Problem?.ErrorFor("historyStartDate") ?? result.Error ?? "The start date couldn’t be saved.";
        return false;
    }

    // Currencies are cached for the whole session (issue #372) — a successful write here has to drop
    // that cache or every picker keeps offering the old list until the app reloads.
    private bool Invalidated(bool saved)
    {
        if (saved)
            ReferenceData.InvalidateCurrencies();
        return saved;
    }
}
