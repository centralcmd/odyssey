using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
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
/// filter or sort change replaces the list, so focus never sits on a row that vanished.
/// </remarks>
public partial class CurrenciesCard
{
    private bool _canReadSync;
    private bool _canWriteSync;
    private bool _canPurge;

    private OdsLiveAnnouncer? _polite;
    private OdsLiveAnnouncer? _assertive;
    private ExchangeRateSyncStatusPanel? _panel;

    // The panel lives in the Overview, and an @ref keeps naming a disposed component after the Overview
    // collapses — so whether one is mounted is the Overview's state, not the reference's.
    private ExchangeRateSyncStatusPanel? MountedPanel => _overviewOpen ? _panel : null;
    private ExchangeRateSyncStatus? _syncStatus;

    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rowErrors = new(StringComparer.Ordinal);

    private ExistingExchangeRateSyncCurrency? _purging;
    private bool _purgeOpen;

    private string? ProviderName => _syncStatus?.ProviderDisplayName;

    private bool ShowingUnpublishedOnly =>
        _statusFilter.Count == 1 && _statusFilter.Contains(nameof(CurrencyStatus.NoLongerPublished));

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

    // A Run now chosen with the Overview collapsed runs once the panel it opened has read status — after
    // a render, by which the reference names the new panel.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_runNowWhenMounted && MountedPanel is { Status: not null } panel)
        {
            _runNowWhenMounted = false;
            await panel.RunNowAsync();
        }
    }

    // ── The header menu's actions. The panel owns polling and lives in the Overview, so an item chosen
    // with it collapsed opens it first: the run starting, or the refreshed status, is then in view. ────

    private bool _runNowWhenMounted;

    private async Task RunSyncNowAsync()
    {
        if (MountedPanel is not { } panel)
        {
            _runNowWhenMounted = true;
            OpenOverview();
            return;
        }
        await panel.RunNowAsync();
    }

    // Mounting the panel reads status, which is the refresh.
    private async Task RefreshSyncStatusAsync()
    {
        if (MountedPanel is not { } panel)
        {
            OpenOverview();
            return;
        }
        await panel.RefreshAsync();
    }

    private void OpenOverview()
    {
        _overviewOpen = true;
        PersistPageState();
        StateHasChanged();
    }

    // A finished run re-reads the page MERGED by code: rows on screen are updated in place, none is
    // inserted or removed until the user's next query.
    private async Task OnRunFinishedAsync(ExchangeRateSyncStatus status)
    {
        _syncStatus = status;
        await MergeCurrenciesAsync();
    }

    private async Task MergeCurrenciesAsync()
    {
        // A load the user started is already bringing fresh rows; merging would supersede it and patch
        // the previous filter's rows instead.
        if (_isLoading || _refetching)
            return;

        var response = await _listLoader.RunAsync(ct => Currencies.ListAsync(
            1, OdsPageSizes.All, search: _search, statuses: _statusFilter, sortBy: _sort.Key,
            sortDir: _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc", ct: ct));
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
    private Task ReloadStatusAsync() => MountedPanel?.ReloadQuietlyAsync() ?? LoadSyncStatusAsync();

    private Task AnnouncePolite(string message) => _polite?.Announce(message) ?? Task.CompletedTask;

    private Task AnnounceAssertive(string message) => _assertive?.Announce(message) ?? Task.CompletedTask;

    private Task ShowUnpublishedAsync() => OnStatusFilterChanged([nameof(CurrencyStatus.NoLongerPublished)]);

    // ── Rows ──────────────────────────────────────────────────────────────────────────────────────

    // Keyed off `locked` and `supported`, never a currency code (SEC F4). A currency the provider does not
    // publish says so in its chip, so only the two rows the chip cannot explain carry a note.
    // The coverage that ends a sync caller's meta line: the fetched span, or "Not fetched yet" for a
    // synced currency with none; nothing for the base, an unsynced currency without rows, or no projection.
    private static string? CoverageText(ExistingExchangeRateSyncCurrency? s) => s switch
    {
        null or { Locked: true } => null,
        { OldestFetchedDate: { } oldest, NewestFetchedDate: { } newest } =>
            $"{ExchangeRateSyncTime.Date(oldest)} – {ExchangeRateSyncTime.Date(newest)}",
        { Enabled: true } => "Not fetched yet",
        _ => null,
    };

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
        if (row is null)
            return;
        row.Sync = updated;
        // The same shared rule the server derives the list's status with, so a patched row cannot read
        // differently from a re-read one.
        row.Status = CurrencyStatuses.Of(row.Archived is not null, updated);
    }

    // Each sync item is offered only when it can act; an unavailable one is omitted, never greyed. The
    // base is never started or stopped, and a currency the provider does not publish can be stopped (the
    // remedy) but not started. The start date is a field of the Edit currency dialog.
    private IEnumerable<OdsMenuItem> SyncActions(ExistingCurrency c)
    {
        if (!_canWriteSync || c.Sync is not { Locked: false } s)
            yield break;

        // The save is started, not awaited: the menu waits out its item's handler before closing, and it
        // should close at once while the row itself says "Saving…".
        if (s.Enabled)
            yield return new OdsMenuItem { Icon = "sync_disabled", Label = "Stop sync", OnClick = EventCallback.Factory.Create(this, () => { _ = ToggleSyncAsync(c, false); }) };
        else if (s.Supported != false)
            yield return new OdsMenuItem { Icon = "sync", Label = "Start sync", OnClick = EventCallback.Factory.Create(this, () => { _ = ToggleSyncAsync(c, true); }) };
    }

    private OdsMenuItem? PurgeAction(ExistingCurrency c) =>
        _canPurge && c.Sync is { Locked: false, Enabled: false, FetchedRowCount: > 0 } s
            ? new OdsMenuItem
            {
                Icon = "delete_sweep", Label = "Delete fetched rates…", Danger = true,
                OnClick = EventCallback.Factory.Create(this, () => StartPurge(s)),
            }
            : null;

    // ── Start / stop ──────────────────────────────────────────────────────────────────────────────

    // Not optimistic: the row carries a "Saving…" chip for the one PUT and changes only on success. The
    // menu returns focus to its own button as it closes. A refusal OPENS the row, whose alert is the
    // reason, so it is never hidden behind a collapsed card.
    private async Task ToggleSyncAsync(ExistingCurrency c, bool enable)
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
            if (_rowErrors.ContainsKey(c.CurrencyCode))
                _openCode = c.CurrencyCode;
        }

        StateHasChanged();
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

    // New or Edit currency saved a start date; the host re-reads the page on the dialog's OnSaved.
    private async Task OnSyncStartSaved(string code)
    {
        await AnnouncePolite($"Start date for {code} saved");
        await ReloadStatusAsync();
    }

    // ── Purge ─────────────────────────────────────────────────────────────────────────────────────

    private void StartPurge(ExistingExchangeRateSyncCurrency s)
    {
        if (!_canPurge || s.Enabled || s.FetchedRowCount == 0)
            return;
        _purging = s;
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

        var code = target.CurrencyCode;
        var result = await Sync.PurgeCurrencyAsync(code);
        if (!result.IsSuccess)
        {
            if (result.Status == HttpStatusCode.Forbidden)
                return ExchangeRateSyncStatusPanel.PermissionMessage;
            return string.IsNullOrWhiteSpace(result.Error) ? "The fetched rates couldn’t be deleted." : result.Error;
        }

        var deleted = result.Value?.RowsDeleted ?? 0;
        ReplaceSync(target with { FetchedRowCount = 0, OldestFetchedDate = null, NewestFetchedDate = null });
        await AnnouncePolite($"Deleted {deleted.ToString("N0", CultureInfo.CurrentCulture)} fetched rates for {code}");
        await ReloadStatusAsync();
        return null;
    }
}
