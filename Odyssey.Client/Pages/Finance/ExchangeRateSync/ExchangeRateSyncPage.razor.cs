using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Odyssey.ApiClient;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance.ExchangeRateSync;

public partial class ExchangeRateSyncPage
{
    private const string PageStateKey = "settings-exchange-rate-sync-page";
    internal const int RunPageSize = 10;

    private OdsLiveAnnouncer? _polite;
    private ExchangeRateSyncStatusPanel? _panel;
    private OdsLiveAnnouncer? _assertive;

    private bool _canUpdate;
    private bool _canPurge;

    private ExchangeRateSyncStatus? _status;

    // ── Currencies ────────────────────────────────────────────────────────────────────────────────
    internal enum ShowSupported { Supported, Enabled, All }

    private static readonly IReadOnlyList<OdsSegmentedOption> ShowOptions =
    [
        new() { Value = nameof(ShowSupported.Supported), Label = "Supported" },
        new() { Value = nameof(ShowSupported.Enabled), Label = "Enabled" },
        new() { Value = nameof(ShowSupported.All), Label = "All" },
    ];

    private static readonly OdsTableSort DefaultCurrencySort = new("code", OdsSortDirection.Asc);

    private List<ExistingExchangeRateSyncCurrency> _currencies = [];
    private bool _currenciesLoading = true;
    private bool _currenciesRefetching;
    private bool _currenciesError;
    private int _currencyPage = 1;
    private int _currencyPageSize = OdsPageSizes.Default[0];
    private int _currencyTotal;
    private string _search = string.Empty;
    private ShowSupported _show = ShowSupported.Supported;
    private OdsTableSort _currencySort = DefaultCurrencySort;
    private readonly ListLoader _listLoader = new();

    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _resetKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rowErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _editErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime?> _editDrafts = new(StringComparer.Ordinal);

    // ── Run history ───────────────────────────────────────────────────────────────────────────────
    private List<ExistingExchangeRateSyncRun> _runs = [];
    private bool _runsLoading = true;
    private bool _runsError;
    private int _runPage = 1;
    private int _runTotal;
    private readonly ListLoader _runLoader = new();

    // ── Purge ─────────────────────────────────────────────────────────────────────────────────────
    private (ExistingExchangeRateSyncCurrency Currency, string MenuTriggerId)? _purging;
    private bool _purgeOpen;

    private static DateTime HistoryFloor => ExchangeRateHistoryStartDateRule.Floor.ToDateTime(TimeOnly.MinValue);

    private DateTime TodayUtc => Time.GetUtcNow().UtcDateTime.Date;

    private bool HasCurrencyFilters => !string.IsNullOrWhiteSpace(_search) || _show != ShowSupported.Supported;

    private string SubLineText => _status is null
        ? string.Empty
        : string.IsNullOrEmpty(_status.Attribution)
            ? $"{_status.EnabledCount} synced"
            : $"{_status.Attribution} · {_status.EnabledCount} synced";

    private IReadOnlyList<string> UnsupportedEnabled => _status?.UnsupportedEnabledCodes ?? [];

    private string UnsupportedBannerText
    {
        get
        {
            var codes = UnsupportedEnabled;
            var list = codes.Count < 2 ? string.Join(string.Empty, codes)
                : $"{string.Join(", ", codes.Take(codes.Count - 1))} and {codes[^1]}";
            var one = codes.Count == 1;
            return $"{list} {(one ? "is" : "are")} no longer published by {_status?.ProviderDisplayName}. Switch {(one ? "it" : "them")} off below.";
        }
    }

    private IReadOnlyList<OdsRecordColumn<ExistingExchangeRateSyncCurrency>> CurrencyColumns =>
    [
        new() { Key = "code", HeaderText = "Currency", Sortable = true, SortType = OdsSortType.Text, Cell = CurrencyCell },
        new() { Key = "enabled", HeaderText = "Sync", Sortable = true, SortType = OdsSortType.Status, Cell = SyncCell },
        new() { Key = "coverage", HeaderText = "Coverage", CellClass = "odc-rec-muted", Cell = CoverageCell },
        new() { Key = "backfill", HeaderText = "Backfill", Cell = BackfillCell },
        new() { Key = "start", HeaderText = "Start date", CellClass = "odc-rec-muted", Cell = StartDateCell },
    ];

    private IReadOnlyList<OdsRecordColumn<ExistingExchangeRateSyncRun>> RunColumns =>
    [
        new() { Key = "started", HeaderText = "Started", Cell = StartedCell },
        new()
        {
            Key = "trigger", HeaderText = "Trigger",
            Cell = (r, _) => b => b.AddContent(0, OdsTypeRegistries.ExchangeRateSyncTriggerOf(r.Trigger)),
        },
        new() { Key = "status", HeaderText = "Status", Cell = RunStatusCell },
        new() { Key = "added", HeaderText = "Added", Align = OdsAlign.End, Cell = (r, _) => b => b.AddContent(0, r.RowsInserted.ToString("N0", CultureInfo.CurrentCulture)) },
        new() { Key = "rejected", HeaderText = "Rejected", Align = OdsAlign.End, Cell = (r, _) => b => b.AddContent(0, r.RowsRejected.ToString("N0", CultureInfo.CurrentCulture)) },
        new() { Key = "deleted", HeaderText = "Deleted", Align = OdsAlign.End, Cell = (r, _) => b => b.AddContent(0, r.RowsDeleted.ToString("N0", CultureInfo.CurrentCulture)) },
        new() { Key = "error", HeaderText = "Error", Cell = RunErrorCell },
    ];

    /// <summary>
    /// Whether the page runs interactively; a swappable seam for the reason <c>Settings.InteractiveCheck</c>
    /// is one. Process-wide, so a test that moves it restores it (<c>ExchangeRateSyncCollection</c>).
    /// </summary>
    internal static Func<bool> InteractiveCheck { get; set; } = static () => OperatingSystem.IsBrowser();

    protected override async Task OnInitializedAsync()
    {
        if (!InteractiveCheck())
            return;

        await PageState.RestoreOrSeedAsync<SyncPageState>(PageStateKey, ApplyPageState, BuildPageState);
        var user = await AuthenticationStateProvider.GetUserAsync();
        _canUpdate = user.HasPermission(PermissionClaims.SystemSettingsUpdate);
        _canPurge = _canUpdate && user.HasPermission(PermissionClaims.ExchangeRatesDelete);
        await Task.WhenAll(LoadCurrenciesAsync(), LoadRunsAsync());
    }

    // ── Page state ────────────────────────────────────────────────────────────────────────────────
    private sealed class SyncPageState
    {
        public string Show { get; set; } = nameof(ShowSupported.Supported);
        public int PageSize { get; set; } = OdsPageSizes.Default[0];
    }

    private void ApplyPageState(SyncPageState state)
    {
        _show = Enum.TryParse<ShowSupported>(state.Show, out var show) ? show : ShowSupported.Supported;
        _currencyPageSize = OdsPageSizes.Restore(state.PageSize);
    }

    private SyncPageState BuildPageState() => new() { Show = _show.ToString(), PageSize = _currencyPageSize };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    // ── Status ────────────────────────────────────────────────────────────────────────────────────
    private void OnStatus(ExchangeRateSyncStatus status)
    {
        _status = status;
        StateHasChanged();
    }

    // A finished run re-reads both tables. The currency page is MERGED by code: rows on screen are
    // updated in place, none is inserted or removed until the user's next query.
    private async Task OnRunFinishedAsync(ExchangeRateSyncStatus status)
    {
        _status = status;
        await Task.WhenAll(MergeCurrenciesAsync(), LoadRunsAsync());
    }

    private Task AnnouncePolite(string message) => _polite?.Announce(message) ?? Task.CompletedTask;

    private Task AnnounceAssertive(string message) => _assertive?.Announce(message) ?? Task.CompletedTask;

    // ── Currency queries ──────────────────────────────────────────────────────────────────────────
    private bool? SupportedFilter => _show == ShowSupported.Supported ? true : null;

    private bool? EnabledFilter => _show == ShowSupported.Enabled ? true : null;

    private string SortKey => _currencySort.Key switch
    {
        "enabled" => nameof(ExchangeRateSyncCurrencySortBy.Enabled),
        "name" => nameof(ExchangeRateSyncCurrencySortBy.Name),
        _ => nameof(ExchangeRateSyncCurrencySortBy.Code),
    };

    private Task<ApiResult<Odyssey.Dtos.PagedResult<ExistingExchangeRateSyncCurrency>>> QueryCurrenciesAsync(CancellationToken ct) =>
        Sync.ListCurrenciesAsync(
            _currencyPage, _currencyPageSize,
            search: _search, enabled: EnabledFilter, supported: SupportedFilter,
            sortBy: SortKey, sortDir: _currencySort.Dir == OdsSortDirection.Asc ? "asc" : "desc", ct: ct);

    private async Task LoadCurrenciesAsync()
    {
        if (!_currenciesLoading)
        {
            _currenciesRefetching = true;
            StateHasChanged();
        }

        var response = await _listLoader.RunAsync(ct => QueryCurrenciesAsync(ct));
        if (response.IsSuperseded)
            return;

        var load = response.Value.PagedOrToast(Snackbar, "currencies");
        if (load.IsSuccess)
        {
            _currencies = [.. load.Items];
            _currencyTotal = load.TotalCount;
            _currenciesError = false;
            _rowErrors.Clear();
        }
        else
        {
            _currenciesError = true;
        }

        _currenciesLoading = false;
        _currenciesRefetching = false;
        StateHasChanged();
    }

    private async Task MergeCurrenciesAsync()
    {
        // A load the user started is already bringing fresh rows; merging would supersede it and patch
        // the previous filter's rows instead.
        if (_currenciesLoading || _currenciesRefetching)
            return;

        var response = await _listLoader.RunAsync(ct => QueryCurrenciesAsync(ct));
        if (response.IsSuperseded)
            return;
        if (!response.Value.IsSuccess || response.Value.Value is not { } page)
            return;

        var fresh = page.Items.ToDictionary(c => c.CurrencyCode, StringComparer.Ordinal);
        _currencies = [.. _currencies.Select(c => fresh.GetValueOrDefault(c.CurrencyCode, c))];
        StateHasChanged();
    }

    private Task ReloadCurrenciesAsync()
    {
        _currencyPage = 1;
        return LoadCurrenciesAsync();
    }

    private void OnSearchChanged(string value) => _search = value ?? string.Empty;

    private Task OnShowChanged(string value)
    {
        _show = Enum.TryParse<ShowSupported>(value, out var show) ? show : ShowSupported.Supported;
        PersistPageState();
        return ReloadCurrenciesAsync();
    }

    private Task ShowEnabledAsync() => OnShowChanged(nameof(ShowSupported.Enabled));

    private Task OnCurrencySortChanged(OdsTableSort sort)
    {
        _currencySort = sort;
        return ReloadCurrenciesAsync();
    }

    private Task OnCurrencyPageChanged(int page)
    {
        _currencyPage = page;
        return LoadCurrenciesAsync();
    }

    private Task OnCurrencyPageSizeChanged(int size)
    {
        _currencyPageSize = size;
        PersistPageState();
        return ReloadCurrenciesAsync();
    }

    private Task ClearCurrencyFiltersAsync()
    {
        _search = string.Empty;
        _show = ShowSupported.All;
        PersistPageState();
        return ReloadCurrenciesAsync();
    }

    // ── Rows ──────────────────────────────────────────────────────────────────────────────────────

    // Keyed off `locked` and `supported`, never a currency code (SEC F4).
    private bool IsLocked(ExistingExchangeRateSyncCurrency c) =>
        c.Locked || (c.Supported == false && !c.Enabled) || !_canUpdate;

    private string? RowNote(ExistingExchangeRateSyncCurrency c)
    {
        if (c.Locked)
            return "Base currency — always synced";
        if (c.Supported == false && c.Enabled)
            return "No longer published — switch off";
        if (c.Supported == false)
            return $"Not published by {_status?.ProviderDisplayName}";
        if (!_canUpdate)
            return "Requires permission to change system settings";
        return null;
    }

    private static string NoteId(ExistingExchangeRateSyncCurrency c) => $"xrs-note-{c.CurrencyCode}";

    private static string RowErrorId(ExistingExchangeRateSyncCurrency c) => $"xrs-err-{c.CurrencyCode}";

    private static string EditFieldId(ExistingExchangeRateSyncCurrency c) => $"xrs-start-{c.CurrencyCode}";

    private string AvailableFromText(ExistingExchangeRateSyncCurrency c) => c.Locked
        ? $"Every {_status?.ProviderDisplayName} rate is quoted against {c.CurrencyCode}"
        : c.FirstAvailableDate is { } first
            ? $"{_status?.ProviderDisplayName} has data from {ExchangeRateSyncTime.Date(first)}"
            : $"{_status?.ProviderDisplayName} doesn’t publish {c.CurrencyCode}";

    private void Replace(ExistingExchangeRateSyncCurrency updated)
    {
        var index = _currencies.FindIndex(c => c.CurrencyCode == updated.CurrencyCode);
        if (index >= 0)
            _currencies[index] = updated;
    }

    private IReadOnlyList<OdsMenuItem> CurrencyActions(ExistingExchangeRateSyncCurrency c, OdsRecordActionContext ctx) =>
    [
        new OdsMenuItem
        {
            Icon = "event", Label = "Set start date…",
            Disabled = c.Locked || !_canUpdate,
            OnClick = EventCallback.Factory.Create(this, ctx.StartEdit),
        },
        new OdsMenuItem { Divider = true },
        new OdsMenuItem
        {
            Icon = "delete_sweep", Label = "Delete fetched rates…", Danger = true,
            Disabled = !(_canPurge && !c.Enabled && c.FetchedRowCount > 0),
            OnClick = EventCallback.Factory.Create(this, () => StartPurge(c, ctx.MenuTriggerId)),
        },
    ];

    // ── Switch ────────────────────────────────────────────────────────────────────────────────────

    // Not optimistic: the switch goes Busy for the one PUT and the model changes only on success. A
    // refusal re-keys the switch so its input is rebuilt from the unchanged model and keeps focus.
    private async Task ToggleAsync(ExistingExchangeRateSyncCurrency c, bool enabled)
    {
        if (_busy.Contains(c.CurrencyCode) || IsLocked(c))
            return;

        _busy.Add(c.CurrencyCode);
        _rowErrors.Remove(c.CurrencyCode);
        StateHasChanged();

        var result = await Sync.UpdateCurrencyAsync(c.CurrencyCode,
            new ExchangeRateSyncCurrencyUpdate { Enabled = enabled, HistoryStartDate = c.HistoryStartDate });
        _busy.Remove(c.CurrencyCode);

        if (result.IsSuccess && result.Value is { } updated)
        {
            Replace(updated);
            await ReloadStatusAsync();
        }
        else
        {
            _resetKeys[c.CurrencyCode] = _resetKeys.GetValueOrDefault(c.CurrencyCode) + 1;
            await ReportRowFailureAsync(c, result, enabled);
        }

        StateHasChanged();
    }

    private async Task ReportRowFailureAsync<T>(ExistingExchangeRateSyncCurrency c, ApiResult<T> result, bool enabled)
    {
        switch (result.Status)
        {
            case HttpStatusCode.Forbidden:
                Snackbar.Add(ExchangeRateSyncStatusPanel.PermissionMessage, Severity.Error);
                await AnnounceAssertive(ExchangeRateSyncStatusPanel.PermissionMessage);
                return;
            case HttpStatusCode.NotFound:
                Snackbar.Add("This currency no longer exists", Severity.Error);
                await LoadCurrenciesAsync();
                return;
            case HttpStatusCode.ServiceUnavailable:
                _rowErrors[c.CurrencyCode] = $"Couldn’t confirm {_status?.ProviderDisplayName} publishes {c.CurrencyCode}. Try again shortly.";
                break;
            default:
                _rowErrors[c.CurrencyCode] = FieldError(result.Problem, "enabled") ?? result.Error ?? $"Couldn’t switch {(enabled ? "on" : "off")} {c.CurrencyCode}.";
                break;
        }

        await AnnounceAssertive($"Couldn’t switch {(enabled ? "on" : "off")} {c.CurrencyCode}. {_rowErrors[c.CurrencyCode]}");
    }

    private static string? FieldError(ApiProblem? problem, string field) => problem?.ErrorFor(field);

    // ── Start date (edit row) ─────────────────────────────────────────────────────────────────────

    private DateTime? EditDraft(ExistingExchangeRateSyncCurrency c) =>
        _editDrafts.TryGetValue(c.CurrencyCode, out var draft)
            ? draft
            : (c.HistoryStartDate ?? c.EffectiveHistoryStartDate).ToDateTime(TimeOnly.MinValue);

    private void SetEditDraft(ExistingExchangeRateSyncCurrency c, DateTime? value)
    {
        _editDrafts[c.CurrencyCode] = value;
        _editErrors.Remove(c.CurrencyCode);
    }

    private void CancelEdit(ExistingExchangeRateSyncCurrency c, OdsRecordEditContext edit)
    {
        _editDrafts.Remove(c.CurrencyCode);
        _editErrors.Remove(c.CurrencyCode);
        edit.Cancel();
    }

    private Task SaveStartDateAsync(ExistingExchangeRateSyncCurrency c, OdsRecordEditContext edit)
    {
        var draft = EditDraft(c);
        if (draft is not { } date || date.Date < HistoryFloor || date.Date > TodayUtc)
        {
            _editErrors[c.CurrencyCode] = $"Enter a date between {ExchangeRateHistoryStartDateRule.Floor.ToString(ExchangeRateHistoryStartDateRule.Format, CultureInfo.InvariantCulture)} and today.";
            return Task.CompletedTask;
        }

        return WriteStartDateAsync(c, edit, DateOnly.FromDateTime(date));
    }

    private Task UseDefaultAsync(ExistingExchangeRateSyncCurrency c, OdsRecordEditContext edit) =>
        WriteStartDateAsync(c, edit, null);

    private async Task WriteStartDateAsync(ExistingExchangeRateSyncCurrency c, OdsRecordEditContext edit, DateOnly? start)
    {
        if (_busy.Contains(c.CurrencyCode))
            return;

        _busy.Add(c.CurrencyCode);
        StateHasChanged();
        var result = await Sync.UpdateCurrencyAsync(c.CurrencyCode,
            new ExchangeRateSyncCurrencyUpdate { Enabled = c.Enabled, HistoryStartDate = start });
        _busy.Remove(c.CurrencyCode);

        if (result.IsSuccess && result.Value is { } updated)
        {
            Replace(updated);
            _editDrafts.Remove(c.CurrencyCode);
            _editErrors.Remove(c.CurrencyCode);
            edit.Done();
            await AnnouncePolite($"Start date for {c.CurrencyCode} saved");
            await ReloadStatusAsync();
        }
        else if (result.Status == HttpStatusCode.Forbidden)
        {
            Snackbar.Add(ExchangeRateSyncStatusPanel.PermissionMessage, Severity.Error);
            await AnnounceAssertive(ExchangeRateSyncStatusPanel.PermissionMessage);
        }
        else
        {
            _editErrors[c.CurrencyCode] = FieldError(result.Problem, "historyStartDate") ?? result.Error ?? "The start date couldn’t be saved.";
            await AnnounceAssertive(_editErrors[c.CurrencyCode]);
        }

        StateHasChanged();
    }

    // ── Purge ─────────────────────────────────────────────────────────────────────────────────────

    private void StartPurge(ExistingExchangeRateSyncCurrency c, string menuTriggerId)
    {
        if (!_canPurge || c.Enabled || c.FetchedRowCount == 0)
            return;
        _purging = (c, menuTriggerId);
        _purgeOpen = true;
    }

    private void OnPurgeOpenChanged(bool open) => _purgeOpen = open;

    private string PurgeBody(ExistingExchangeRateSyncCurrency c) =>
        $"This removes {c.FetchedRowCount.ToString("N0", CultureInfo.CurrentCulture)} rates fetched from {_status?.ProviderDisplayName}. "
        + $"Manually entered {c.CurrencyCode} rates are kept. Switching {c.CurrencyCode} on again fetches its history again.";

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
        Replace(target.Currency with { FetchedRowCount = 0, OldestFetchedDate = null, NewestFetchedDate = null });
        await AnnouncePolite($"Deleted {deleted.ToString("N0", CultureInfo.CurrentCulture)} fetched rates for {code}");
        await Task.WhenAll(LoadRunsAsync(), ReloadStatusAsync());
        return null;
    }

    // The chip, the banners and the synced count are read from status, which the panel polls only
    // during a run — so a write that changes them re-reads it once.
    private Task ReloadStatusAsync() => _panel?.ReloadQuietlyAsync() ?? Task.CompletedTask;

    // ── Run history ───────────────────────────────────────────────────────────────────────────────

    private async Task LoadRunsAsync()
    {
        var response = await _runLoader.RunAsync(ct => Sync.ListRunsAsync(_runPage, RunPageSize, ct));
        if (response.IsSuperseded)
            return;

        var load = response.Value.PagedOrToast(Snackbar, "run history");
        if (load.IsSuccess)
        {
            _runs = [.. load.Items];
            _runTotal = load.TotalCount;
            _runsError = false;
        }
        else
        {
            _runsError = true;
        }

        _runsLoading = false;
        StateHasChanged();
    }

    private Task OnRunPageChanged(int page)
    {
        _runPage = page;
        return LoadRunsAsync();
    }

    private IReadOnlyList<OdsMenuItem> RunActions(ExistingExchangeRateSyncRun r, OdsRecordActionContext ctx) =>
    [
        new OdsMenuItem { Icon = r.Status == ExchangeRateSyncRunStatus.Running ? "hourglass_top" : "unfold_more", Label = ctx.Expanded ? "Hide details" : "View details", OnClick = EventCallback.Factory.Create(this, ctx.Toggle) },
    ];

    private static string SpanText(ExistingExchangeRateSyncRun r) => (r.SpanFrom, r.SpanTo) switch
    {
        ({ } from, { } to) when from == to => ExchangeRateSyncTime.Date(from),
        ({ } from, { } to) => $"{ExchangeRateSyncTime.Date(from)} – {ExchangeRateSyncTime.Date(to)}",
        _ => "—",
    };

    public void Dispose()
    {
        _listLoader.Dispose();
        _runLoader.Dispose();
    }
}
