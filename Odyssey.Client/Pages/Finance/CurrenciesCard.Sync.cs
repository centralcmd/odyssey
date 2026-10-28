using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Components;
using Odyssey.Client.Pages.Finance.ExchangeRateSync;
using Odyssey.Dtos;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// Exchange-rate sync on the Currencies page (issue #351; Odyssey Design System ·
/// docs/components-exchange-rate-sync.md, "Currencies"). It used to be its own page at
/// /settings/exchange-rate-sync; both tables listed one row per currency, so it was merged here.
/// </summary>
/// <remarks>
/// Everything here is behind <c>system-settings.read</c>, and absent — not greyed — without it: the server
/// attaches no <see cref="ExistingCurrency.Sync"/> projection and refuses the sync filter and sort.
/// Edits stay in place: a sync change or a purge patches its row from the response, a start date saved
/// in Edit currency re-reads the current page as every edit there does, and only the user's own search,
/// filter, sort or page change resets to page 1, so focus never sits on a row that vanished.
/// </remarks>
public partial class CurrenciesCard
{
    private bool _canReadSync;
    private bool _canWriteSync;
    private bool _canPurge;

    private OdsLiveAnnouncer? _polite;
    private OdsLiveAnnouncer? _assertive;
    private ExchangeRateSyncStatusPanel? _panel;
    private ExchangeRateSyncRunHistory? _runHistory;
    private ExchangeRateSyncStatus? _syncStatus;

    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rowErrors = new(StringComparer.Ordinal);

    private (ExistingExchangeRateSyncCurrency Currency, string MenuTriggerId)? _purging;
    private bool _purgeOpen;

    // The Sync filter's values are the server's CurrencySyncState names.
    private static readonly IReadOnlyList<OdsOption> SyncOptions =
    [
        .. Enum.GetValues<CurrencySyncState>()
            .Select(state => new OdsOption(state.ToString(), OdsTypeRegistries.CurrencySyncStateOf(state).Label)),
    ];

    private string? ProviderName => _syncStatus?.ProviderDisplayName;

    private bool ShowingSyncedOnly =>
        _syncFilter.Count == 1 && _syncFilter.Contains(nameof(CurrencySyncState.On));

    private IReadOnlyList<string> UnsupportedEnabled => _syncStatus?.UnsupportedEnabledCodes ?? [];

    private string UnsupportedBannerText
    {
        get
        {
            var codes = UnsupportedEnabled;
            var list = codes.Count < 2 ? string.Join(string.Empty, codes)
                : $"{string.Join(", ", codes.Take(codes.Count - 1))} and {codes[^1]}";
            var one = codes.Count == 1;
            return $"{list} {(one ? "is" : "are")} no longer published by {ProviderName}. Stop syncing {(one ? "it" : "them")} from the row menu.";
        }
    }

    // ── Status ────────────────────────────────────────────────────────────────────────────────────

    // The panel reads and polls status while the Overview is open; the page reads it once itself so the
    // banners and the synced count are right with the Overview collapsed too.
    private async Task LoadSyncStatusAsync()
    {
        if (!_canReadSync)
            return;
        var result = await Sync.GetStatusAsync();
        if (result.IsSuccess && result.Value is { } status)
        {
            _syncStatus = status;
            StateHasChanged();
        }
    }

    private void OnSyncStatus(ExchangeRateSyncStatus status)
    {
        _syncStatus = status;
        StateHasChanged();
    }

    // A finished run re-reads the page MERGED by code: rows on screen are updated in place, none is
    // inserted or removed until the user's next query.
    private async Task OnRunFinishedAsync(ExchangeRateSyncStatus status)
    {
        _syncStatus = status;
        await Task.WhenAll(MergeCurrenciesAsync(), ReloadRunsAsync());
    }

    private async Task MergeCurrenciesAsync()
    {
        // A load the user started is already bringing fresh rows; merging would supersede it and patch
        // the previous filter's rows instead.
        if (_isLoading || _refetching)
            return;

        var response = await _listLoader.RunAsync(ct => Currencies.ListAsync(
            _page, _pageSize, search: _search, status: _statusFilter, sortBy: _sort.Key,
            sortDir: _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc", sync: _syncFilter, ct: ct));
        if (response.IsSuperseded)
            return;
        if (!response.Value.IsSuccess || response.Value.Value is not { } page)
            return;

        var fresh = page.Items.ToDictionary(c => c.CurrencyCode, StringComparer.Ordinal);
        _currencies = [.. _currencies.Select(c => fresh.GetValueOrDefault(c.CurrencyCode, c))];
        StateHasChanged();
    }

    // The chips, the banners and the synced count are read from status, which the panel polls only
    // during a run — so a write that changes them re-reads it once.
    private Task ReloadStatusAsync() => _panel?.ReloadQuietlyAsync() ?? LoadSyncStatusAsync();

    private Task ReloadRunsAsync() => _runHistory?.ReloadAsync() ?? Task.CompletedTask;

    private Task AnnouncePolite(string message) => _polite?.Announce(message) ?? Task.CompletedTask;

    private Task AnnounceAssertive(string message) => _assertive?.Announce(message) ?? Task.CompletedTask;

    private Task ShowSyncedAsync() => OnSyncFilterChanged([nameof(CurrencySyncState.On)]);

    // ── Rows ──────────────────────────────────────────────────────────────────────────────────────

    // Keyed off `locked` and `supported`, never a currency code (SEC F4). A currency the provider does not
    // publish says so in its chip, so only the two rows the chip cannot explain carry a note.
    private string? RowNote(ExistingExchangeRateSyncCurrency s)
    {
        if (s.Locked)
            return $"{ProviderName}’s base — always synced";
        if (s.Supported == false && s.Enabled)
            return "No longer published — stop syncing";
        return null;
    }

    private string AvailableFromText(ExistingExchangeRateSyncCurrency s) => s.Locked
        ? $"Every {ProviderName} rate is quoted against {s.CurrencyCode}"
        : s.FirstAvailableDate is { } first
            ? $"{ProviderName} has data from {ExchangeRateSyncTime.Date(first)}"
            : $"{ProviderName} doesn’t publish {s.CurrencyCode}";

    private void ReplaceSync(ExistingExchangeRateSyncCurrency updated)
    {
        var row = _currencies.FirstOrDefault(c => c.CurrencyCode == updated.CurrencyCode);
        if (row is not null)
            row.Sync = updated;
    }

    // Each sync item is offered only when it can act; an unavailable one is omitted, never greyed. The
    // base is never started or stopped, and a currency the provider does not publish can be stopped (the
    // remedy) but not started. The start date is a field of the Edit currency dialog.
    private IEnumerable<OdsMenuItem> SyncActions(ExistingCurrency c, OdsRecordActionContext ctx)
    {
        if (!_canWriteSync || c.Sync is not { Locked: false } s)
            yield break;

        // The save is started, not awaited: the menu waits out its item's handler before closing, and it
        // should close at once while the row itself says "Saving…".
        if (s.Enabled)
            yield return new OdsMenuItem { Icon = "sync_disabled", Label = "Stop sync", OnClick = EventCallback.Factory.Create(this, () => { _ = ToggleSyncAsync(c, false, ctx.MenuTriggerId); }) };
        else if (s.Supported != false)
            yield return new OdsMenuItem { Icon = "sync", Label = "Start sync", OnClick = EventCallback.Factory.Create(this, () => { _ = ToggleSyncAsync(c, true, ctx.MenuTriggerId); }) };
    }

    private OdsMenuItem? PurgeAction(ExistingCurrency c, OdsRecordActionContext ctx) =>
        _canPurge && c.Sync is { Locked: false, Enabled: false, FetchedRowCount: > 0 } s
            ? new OdsMenuItem
            {
                Icon = "delete_sweep", Label = "Delete fetched rates…", Danger = true,
                OnClick = EventCallback.Factory.Create(this, () => StartPurge(s, ctx.MenuTriggerId)),
            }
            : null;

    // ── Start / stop ──────────────────────────────────────────────────────────────────────────────

    // Not optimistic: the cell reads "Saving…" for the one PUT and the row changes only on success. Focus
    // returns to the row's menu button either way, since the item that started it unmounted with its menu.
    private async Task ToggleSyncAsync(ExistingCurrency c, bool enable, string menuTriggerId)
    {
        if (c.Sync is not { } s || _busy.Contains(c.CurrencyCode) || !_canWriteSync || s.Locked)
            return;

        _busy.Add(c.CurrencyCode);
        _rowErrors.Remove(c.CurrencyCode);
        StateHasChanged();

        var result = await Sync.UpdateCurrencyAsync(c.CurrencyCode,
            new ExchangeRateSyncCurrencyUpdate { Enabled = enable, HistoryStartDate = s.HistoryStartDate });
        _busy.Remove(c.CurrencyCode);

        if (result.IsSuccess && result.Value is { } updated)
        {
            ReplaceSync(updated);
            await AnnouncePolite(enable ? $"Sync started for {c.CurrencyCode}" : $"Sync stopped for {c.CurrencyCode}");
            await ReloadStatusAsync();
        }
        else
        {
            await ReportRowFailureAsync(c, result, enable);
        }

        StateHasChanged();
        await FocusAsync(menuTriggerId);
    }

    private async Task ReportRowFailureAsync<T>(ExistingCurrency c, ApiResult<T> result, bool enable)
    {
        switch (result.Status)
        {
            case HttpStatusCode.Forbidden:
                Snackbar.Add(ExchangeRateSyncStatusPanel.PermissionMessage, Severity.Error);
                await AnnounceAssertive(ExchangeRateSyncStatusPanel.PermissionMessage);
                return;
            case HttpStatusCode.NotFound:
                Snackbar.Add("This currency no longer exists", Severity.Error);
                await GetCurrencies();
                return;
            case HttpStatusCode.ServiceUnavailable:
                _rowErrors[c.CurrencyCode] = $"Couldn’t confirm {ProviderName} publishes {c.CurrencyCode}. Try again shortly.";
                break;
            default:
                _rowErrors[c.CurrencyCode] = result.Problem?.ErrorFor("enabled") ?? result.Error ?? $"Couldn’t {(enable ? "start" : "stop")} syncing {c.CurrencyCode}.";
                break;
        }

        await AnnounceAssertive($"Couldn’t {(enable ? "start" : "stop")} syncing {c.CurrencyCode}. {_rowErrors[c.CurrencyCode]}");
    }

    private async Task FocusAsync(string elementId)
    {
        try { await Js.InvokeVoidAsync("odsFocusById", elementId); }
        catch { /* JS unavailable (e.g. prerender / teardown) */ }
    }

    // New or Edit currency saved a start date; the host re-reads the page on the dialog's OnSaved.
    private async Task OnSyncStartSaved(string code)
    {
        await AnnouncePolite($"Start date for {code} saved");
        await ReloadStatusAsync();
    }

    // ── Purge ─────────────────────────────────────────────────────────────────────────────────────

    private void StartPurge(ExistingExchangeRateSyncCurrency s, string menuTriggerId)
    {
        if (!_canPurge || s.Enabled || s.FetchedRowCount == 0)
            return;
        _purging = (s, menuTriggerId);
        _purgeOpen = true;
    }

    private void OnPurgeOpenChanged(bool open) => _purgeOpen = open;

    private string PurgeBody(ExistingExchangeRateSyncCurrency s) =>
        $"This removes {s.FetchedRowCount.ToString("N0", CultureInfo.CurrentCulture)} rates fetched from {ProviderName}. "
        + $"Manually entered {s.CurrencyCode} rates are kept. Starting {s.CurrencyCode}’s sync again fetches its history again.";

    private async Task<string?> ConfirmPurgeAsync()
    {
        if (_purging is not { } target)
            return null;

        var code = target.Currency.CurrencyCode;
        var result = await Sync.PurgeCurrencyAsync(code);
        if (!result.IsSuccess)
        {
            if (result.Status == HttpStatusCode.Forbidden)
                return ExchangeRateSyncStatusPanel.PermissionMessage;
            return string.IsNullOrWhiteSpace(result.Error) ? "The fetched rates couldn’t be deleted." : result.Error;
        }

        var deleted = result.Value?.RowsDeleted ?? 0;
        ReplaceSync(target.Currency with { FetchedRowCount = 0, OldestFetchedDate = null, NewestFetchedDate = null });
        await AnnouncePolite($"Deleted {deleted.ToString("N0", CultureInfo.CurrentCulture)} fetched rates for {code}");
        await Task.WhenAll(ReloadRunsAsync(), ReloadStatusAsync());
        return null;
    }
}
