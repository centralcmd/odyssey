using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class SubscriptionCard
{
    // ── Data ────────────────────────────────────────────────────────────────
    private List<SubscriptionListItem> _subscriptions = [];        // server-filtered display list
    private SubscriptionSummary? _summary;                         // server-computed counts / run-rate / renewals
    private readonly Dictionary<Guid, ExistingSubscription> _details = new();

    private IReadOnlyList<OdsOption> _companyOptions = [];
    private List<ExistingCurrency> _currencies = [];

    private Guid? _flashId;

    // Card-list windowing (OdsInfiniteList): "Load N at a time" batch size.
    private int _batch = OdsPageSizes.Batch[0];

    // ── UI state ─────────────────────────────────────────────────────────────
    private bool _isLoading = true;
    private bool _refetching;
    private bool _loadError;
    private string _announce = "";
    private Guid? _expandedId;

    // ── Persisted page state ───────────────────────────────────────────────────
    private const string PageStateKey = "subscriptions-page";
    private bool _problemsOpen = true;
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _searchString = string.Empty;
    private IReadOnlyCollection<string> _intervalFilter = [];
    private IReadOnlyCollection<string> _statusFilter = [];

    // Single derived-status filter (Active / Paused / Ended / Archived; empty = all) — matches the
    // design system. Values are the SubscriptionStatusFilter enum names, sent multi via AddMany.
    private static readonly IReadOnlyList<OdsOption> _statusOptions =
    [
        new(nameof(SubscriptionStatusFilter.Active), "Active"),
        new(nameof(SubscriptionStatusFilter.Paused), "Paused"),
        new(nameof(SubscriptionStatusFilter.Ended), "Ended"),
        new(nameof(SubscriptionStatusFilter.Archived), "Archived"),
    ];

    // ── Sort — toolbar OdsSortSelect is the sole sort surface. Keys map 1:1 to SubscriptionSortBy;
    //    "Frequency" sorts by the interval's numeric enum order. ──
    private static readonly OdsTableSort DefaultSort = new("name", OdsSortDirection.Asc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<SubscriptionListItem>> _sortFields =
    [
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text, SortValue = s => s.Name.ToLowerInvariant() },
        new() { Key = "amount", Label = "Price", Type = OdsSortType.Number, SortValue = s => s.Amount },
        new() { Key = "startDate", Label = "Start date", Type = OdsSortType.Date, SortValue = s => s.StartDate.ToDateTime(TimeOnly.MinValue) },
        new() { Key = "interval", Label = "Frequency", Type = OdsSortType.Status, SortValue = s => (int)s.Interval },
    ];

    // ── Permissions ────────────────────────────────────────────────────────────
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;

    // ── Computed (server summary) ──────────────────────────────────────────────────
    private int _activeCount => _summary?.CountsByStatus.Active ?? 0;
    private int _pausedCount => _summary?.CountsByStatus.Paused ?? 0;
    private int _endedCount => _summary?.CountsByStatus.Ended ?? 0;
    private int _archivedCount => _summary?.CountsByStatus.Archived ?? 0;

    // "Today" in UTC to match the server's derivation (SubscriptionService uses TimeProvider.GetUtcNow),
    // so the client chip / row-action gating never disagrees with the server counts across the date
    // boundary. Same convention as ContractsCard / InsuranceCard.
    private static DateOnly TodayUtc => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>Derived terminal state (never stored): a subscription is Ended once its end date is
    /// set and on/before today (UTC). Mirrors the server's derivation for the chip / row action.</summary>
    private static bool IsEnded(DateOnly? endDate) => endDate is { } e && e <= TodayUtc;
    // True when there is anything behind the filter. Primarily driven by the summary, but falls back
    // to the loaded display list so a transient /summary failure (swallowed to null) doesn't misrender
    // a populated list as the "No subscriptions yet" empty state.
    private bool _hasAnySubscriptions =>
        (_summary is { } s && s.Total + s.CountsByStatus.Archived > 0) || _subscriptions.Count > 0;
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_searchString)
        || _intervalFilter.Count > 0 || _statusFilter.Count > 0;

    // "By interval" over the live set (present intervals only, enum order); "By status" over the
    // derived single status. Both from the server summary so they stay stable across list filters.
    private IReadOnlyList<OdsBreakdownRow> IntervalRows =>
        (_summary?.CountsByInterval ?? [])
            .Where(r => r.Count > 0)
            .OrderBy(r => (int)r.Interval)
            .Select(r =>
            {
                var m = OdsTypeRegistries.BillingIntervalOf(r.Interval);
                return new OdsBreakdownRow { Key = r.Interval, Icon = m.Icon, IconColor = m.Color, Label = m.Label, Count = r.Count };
            })
            .ToList();

    private IReadOnlyList<OdsBreakdownRow> StatusRows
    {
        get
        {
            var c = _summary?.CountsByStatus;
            return
            [
                new OdsBreakdownRow { Key = "active", Icon = "autorenew", IconColor = OdsBreakdown.Tone("income"), Label = "Active", Count = c?.Active ?? 0 },
                new OdsBreakdownRow { Key = "paused", Icon = "pause_circle", IconColor = OdsBreakdown.Tone("pending"), Label = "Paused", Count = c?.Paused ?? 0 },
                new OdsBreakdownRow { Key = "ended", Icon = "event_busy", IconColor = OdsBreakdown.Tone("expense"), Label = "Ended", Count = c?.Ended ?? 0 },
                new OdsBreakdownRow { Key = "archived", Icon = "inventory_2", IconColor = OdsBreakdown.Tone("outline"), Label = "Archived", Count = c?.Archived ?? 0 },
            ];
        }
    }

    // ── Run-rate display (blended into the user's display currency) ───────────────────
    private bool RunRateMulti => (_summary?.RunRate.Rows.Count ?? 0) > 1;
    private string RunRateMonthly => FormatRunRate(_summary?.RunRate.ConvertedMonthly);
    private string RunRateYearly => FormatRunRate(_summary?.RunRate.ConvertedYearly);

    private string FormatRunRate(decimal? value) =>
        value is { } amount && _summary is { } s ? $"{(RunRateMulti ? "≈ " : "")}{Money(amount, s.RunRate.BaseCurrency)}" : "—";

    private string RunRateMonthlyFoot =>
        _summary is not { } s ? string.Empty
            : s.RunRate.TopDriver is { } d ? $"Largest: {d.Name}"
            : $"in {s.RunRate.BaseCurrency}";

    private string RunRateYearlyFoot =>
        _summary is not { } s ? string.Empty
            : s.RunRate.ExcludedCurrencies.Count > 0
                ? $"in {s.RunRate.BaseCurrency} · {string.Join(", ", s.RunRate.ExcludedCurrencies)} excluded"
                : $"in {s.RunRate.BaseCurrency}";

    // Upcoming renewals → the PageHeader problem rollup (informational). Cached; rebuilt per summary
    // load by RebuildRenewalProblems (each row jumps to its card).
    private List<PageHeaderProblem>? _renewalProblems;
    private List<PageHeaderProblem>? RenewalProblems => _renewalProblems;

    private static string RelativeDay(int days) => days switch
    {
        <= 0 => "today",
        1 => "tomorrow",
        _ => $"in {days} days",
    };

    // ── Lifecycle ────────────────────────────────────────────────────────────────
    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await RestorePageStateAsync();
        await LoadPermissionsAsync();
        // Currencies first so the run-rate/prices render with their symbols on the first paint.
        await LoadCurrencies();
        await Task.WhenAll(LoadSubscriptions(), LoadSummary(), LoadCompanies());
    }

    // ── Page-state persistence ─────────────────────────────────────────────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<SubscriptionsPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(SubscriptionsPageState state)
    {
        _problemsOpen = state.ProblemsOpen;
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _searchString = state.Search ?? string.Empty;
        _intervalFilter = OdsTypeRegistries.BillingIntervalOptions.KnownValues(state.IntervalFilter);
        _statusFilter = _statusOptions.KnownValues(state.StatusFilter);
        _sort = OdsSortHelpers.Resolve(_sortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private SubscriptionsPageState BuildPageState() => new()
    {
        ProblemsOpen = _problemsOpen,
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _searchString,
        IntervalFilter = [.. _intervalFilter],
        StatusFilter = [.. _statusFilter],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        BatchSize = _batch,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnProblemsToggled(bool open) { _problemsOpen = open; PersistPageState(); }
    private void OnOverviewToggled(bool open) { _overviewOpen = open; PersistPageState(); }
    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _searchString = value ?? string.Empty; PersistPageState(); }
    private async Task OnIntervalFilterChanged(IReadOnlyCollection<string> values) { _intervalFilter = values ?? []; PersistPageState(); await LoadSubscriptions(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await LoadSubscriptions(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await LoadSubscriptions(); }
    private void OnBatchChanged(int size) { _batch = size; PersistPageState(); StateHasChanged(); }

    private sealed class SubscriptionsPageState
    {
        public bool ProblemsOpen { get; set; } = true;
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> IntervalFilter { get; set; } = [];
        public List<string> StatusFilter { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int BatchSize { get; set; } = OdsPageSizes.Batch[0];
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.SubscriptionsCreate);
        _canUpdate = user.HasPermission(PermissionClaims.SubscriptionsUpdate);
        _canDelete = user.HasPermission(PermissionClaims.SubscriptionsDelete);
    }

    // Server-side (issue #277): search + interval filter + scalar status/paused + sort applied by the API.
    private async Task LoadSubscriptions()
    {
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        // Track failure explicitly: ItemsOrToast falls back to [], which is indistinguishable from a
        // genuinely empty set and would render the onboarding empty state after a 500.
        var result = await Subscriptions.ListAsync(
            _searchString,
            _intervalFilter,
            _statusFilter,
            _sort.Key,
            _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc");

        _subscriptions = result.ItemsOrToast(Snackbar, "subscriptions");
        _loadError = !result.IsSuccess;

        _announce = _loadError ? "Couldn't load subscriptions."
            : _subscriptions.Count == 0 ? "No subscriptions match your filters."
            : $"Showing {_subscriptions.Count} subscription{(_subscriptions.Count == 1 ? "" : "s")}.";
        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    // Server summary (counts + run-rate + upcoming renewals), blended into the user's display currency.
    private async Task LoadSummary()
    {
        _summary = await Subscriptions.GetSummaryAsync(UserPreferences.DefaultCurrency);
        RebuildRenewalProblems();
        StateHasChanged();
    }

    // Rebuild the upcoming-renewals rollup once per summary load rather than per render — each
    // PageHeaderProblem carries a fresh EventCallback, so recomputing on every render would defeat
    // PageHeader's parameter diffing.
    private void RebuildRenewalProblems() =>
        _renewalProblems = _summary is null ? null :
        [.. _summary.UpcomingRenewals.Select(r => new PageHeaderProblem
        {
            Severity = PageHeaderSeverity.Information,
            Lead = r.Name,
            Message = $"{Money(r.Amount, r.CurrencyCode)} · {RelativeDay(r.DaysUntil)} ({r.NextBillingDate:MMM dd})",
            OnView = EventCallback.Factory.Create(this, () => JumpTo(r.SubscriptionId)),
        })];

    private async Task LoadCompanies()
    {
        var contacts = await ReferenceData.ContactsAsync();
        _companyOptions =
        [
            .. contacts
                .Where(c => c.Archived is null)
                .OrderBy(c => c.ResolvedDisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(c =>
                {
                    var meta = OdsTypeRegistries.ContactTypeOf(c.Type.ToString());
                    return new OdsOption(c.ContactId.ToString(), c.ResolvedDisplayName) { Icon = meta.Icon, IconColor = meta.Color };
                })
        ];
    }

    private async Task LoadCurrencies()
    {
        _currencies = [.. await ReferenceData.ActiveCurrenciesAsync()];
    }

    // ── Expand / detail load ─────────────────────────────────────────────────────
    private bool IsExpanded(Guid id) => _expandedId == id;

    private async Task ToggleExpand(Guid id)
    {
        if (_expandedId == id)
        {
            _expandedId = null;
            return;
        }

        _expandedId = id;
        await EnsureDetail(id);
    }

    private async Task EnsureDetail(Guid id)
    {
        if (_details.ContainsKey(id))
            return;

        // Announce through the always-mounted OdsLiveAnnouncer: a live region created together with its
        // text (the per-row detail spinner) is not reliably announced, so route the status here.
        _announce = "Loading subscription details…";
        StateHasChanged();

        var subscription = await Subscriptions.GetAsync(id);
        if (subscription is not null)
            _details[id] = subscription;
        StateHasChanged();
    }

    private async Task ReloadSubscription(Guid id)
    {
        var subscription = await Subscriptions.GetAsync(id);
        if (subscription is not null)
            _details[id] = subscription;
        await LoadSubscriptions();
        await LoadSummary();
        StateHasChanged();
    }

    // Jump to a subscription card from the upcoming-renewals rollup: expand, flash, scroll.
    private async Task JumpTo(Guid id)
    {
        await EnsureDetail(id);
        _expandedId = id;
        _flashId = id;
        StateHasChanged();

        try
        {
            await ScrollManager.ScrollIntoViewAsync($"#sub-{id}", ScrollBehavior.Smooth);
        }
        catch (Exception)
        {
            // Best-effort scroll; the record is already expanded.
        }

        await Task.Delay(OdsTiming.RowFlashMs);
        if (_flashId == id)
        {
            _flashId = null;
            StateHasChanged();
        }
    }

    // ── Create ──────────────────────────────────────────────────────────────────
    private Guid _createKey;
    private bool _createOpen;

    private void AddClicked()
    {
        if (!_canCreate) return;
        _createKey = Guid.NewGuid();
        _createOpen = true;
    }

    private async Task OnSubscriptionCreated()
    {
        await LoadSubscriptions();
        await LoadSummary();
    }

    // ── Edit (design-system update: the create dialog reused in edit mode, not an inline panel) ──
    private ExistingSubscription? _editSubscription;
    private Guid _editSubscriptionKey;
    private bool _editSubscriptionOpen;

    private async Task EditClicked(SubscriptionListItem s)
    {
        if (!_canUpdate) return;

        // The dialog prefills from the full record, so make sure the detail is loaded first.
        await EnsureDetail(s.SubscriptionId);
        if (!_details.TryGetValue(s.SubscriptionId, out var detail))
            return;

        _editSubscription = detail;
        _editSubscriptionKey = Guid.NewGuid();
        _editSubscriptionOpen = true;
    }

    private async Task OnSubscriptionEdited()
    {
        if (_editSubscription is not { } edited)
            return;

        _announce = "Subscription updated.";
        await ReloadSubscription(edited.SubscriptionId);
    }

    // ── Pause / resume (PUT with the Paused flag; the service owns the timestamp) ──────
    private async Task TogglePause(SubscriptionListItem s)
    {
        if (!_canUpdate) return;
        await EnsureDetail(s.SubscriptionId);
        if (!_details.TryGetValue(s.SubscriptionId, out var d)) return;

        var pausing = s.Paused is null;
        var update = BuildUpdate(SubscriptionDraft.From(d), d.Amount, pausing, s.Archived is not null);

        if ((await Subscriptions.UpdateAsync(s.SubscriptionId, update)).Toast(Snackbar,
                pausing ? "Unable to pause subscription" : "Unable to resume subscription",
                pausing ? "Subscription paused." : "Subscription resumed."))
        {
            _announce = pausing ? "Subscription paused." : "Subscription resumed.";
            await ReloadSubscription(s.SubscriptionId);
        }
    }

    // ── End (PUT with EndDate = today; the term lapses immediately → derived Ended state) ──────
    private async Task EndSubscription(SubscriptionListItem s)
    {
        if (!_canUpdate) return;
        await EnsureDetail(s.SubscriptionId);
        if (!_details.TryGetValue(s.SubscriptionId, out var d)) return;

        var draft = SubscriptionDraft.From(d);
        // UTC to match the server's Ended derivation → the chip flips immediately after the PUT.
        draft.EndDate = DateTime.UtcNow.Date;
        var update = BuildUpdate(draft, d.Amount, s.Paused is not null, s.Archived is not null);

        if ((await Subscriptions.UpdateAsync(s.SubscriptionId, update)).Toast(Snackbar,
                "Unable to end subscription", "Subscription ended."))
        {
            _announce = "Subscription ended.";
            await ReloadSubscription(s.SubscriptionId);
        }
    }

    // ── Archive / restore (PUT with the Archived flag; archived rows stay in the list, dimmed) ──────
    private async Task ToggleArchive(SubscriptionListItem s)
    {
        if (!_canUpdate) return;
        await EnsureDetail(s.SubscriptionId);
        if (!_details.TryGetValue(s.SubscriptionId, out var d)) return;

        var archiving = s.Archived is null;
        var update = BuildUpdate(SubscriptionDraft.From(d), d.Amount, s.Paused is not null, archiving);

        if ((await Subscriptions.UpdateAsync(s.SubscriptionId, update)).Toast(Snackbar,
                archiving ? "Unable to archive subscription" : "Unable to restore subscription",
                archiving ? "Subscription archived." : "Subscription restored."))
        {
            _announce = archiving ? "Subscription archived." : "Subscription restored.";
            await ReloadSubscription(s.SubscriptionId);
        }
    }

    // Build an UpdateSubscription from a draft, preserving the (UI-hidden) cadence multiplier and
    // applying the given pause/archive flags — the service owns the actual timestamps.
    private static UpdateSubscription BuildUpdate(SubscriptionDraft draft, decimal amount, bool paused, bool archived) => new()
    {
        Name = draft.Name!.Trim(),
        ExternalId = string.IsNullOrWhiteSpace(draft.ExternalId) ? null : draft.ExternalId!.Trim(),
        ContactId = Guid.TryParse(draft.ContactId, out var cp) ? cp : null,
        StartDate = DateOnly.FromDateTime(draft.StartDate!.Value),
        EndDate = draft.EndDate is { } e ? DateOnly.FromDateTime(e) : null,
        Amount = amount,
        CurrencyCode = draft.CurrencyCode.Trim().ToUpperInvariant(),
        Interval = Enum.TryParse<BillingInterval>(draft.Interval, out var iv) ? iv : BillingInterval.Monthly,
        IntervalCount = draft.IntervalCount,
        FirstBillingDate = DateOnly.FromDateTime(draft.FirstBillingDate!.Value),
        Notes = string.IsNullOrWhiteSpace(draft.Notes) ? null : draft.Notes!.Trim(),
        Paused = paused,
        Archived = archived,
    };

    // ── Delete (permanent) ──────────────────────────────────────────────────────
    private async Task ConfirmDelete(SubscriptionListItem s)
    {
        if (!_canDelete) return;
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete subscription",
            $"Permanently delete '{s.Name}'? This cannot be undone.",
            yesText: "Delete", cancelText: "Cancel");

        if (confirmed == true && (await Subscriptions.DeleteAsync(s.SubscriptionId)).Toast(Snackbar, "Delete failed", "Subscription deleted."))
        {
            _announce = "Subscription deleted.";
            _subscriptions.Remove(s);
            _details.Remove(s.SubscriptionId);
            if (_expandedId == s.SubscriptionId) _expandedId = null;
            if (_editSubscription?.SubscriptionId == s.SubscriptionId)
            {
                _editSubscription = null;
                _editSubscriptionOpen = false;
            }
            await LoadSummary();
            StateHasChanged();
        }
    }

    private Task CopyId(Guid id) => Clipboard.CopyAsync(id.ToString(), "Subscription ID copied.");

    private async Task ClearFilters()
    {
        _searchString = string.Empty;
        _intervalFilter = [];
        _statusFilter = [];
        PersistPageState();
        await LoadSubscriptions();
    }

    // ── Money / label helpers ─────────────────────────────────────────────────────
    private string Money(decimal amount, string? code)
    {
        var symbol = _currencies.FirstOrDefault(c => string.Equals(c.CurrencyCode, code, StringComparison.OrdinalIgnoreCase))?.Symbol
            ?? code ?? string.Empty;
        return $"{symbol} {amount.ToString("#,##0.##", CultureInfo.InvariantCulture)}".Trim();
    }

    // ── Lifecycle-action draft ────────────────────────────────────────────────────
    // The pause / end / archive row actions PUT the whole record, so they round-trip the
    // current values through this shape before applying their one change.
    private sealed class SubscriptionDraft
    {
        public string? Name { get; set; }
        public string? ExternalId { get; set; }
        public string? ContactId { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string? Amount { get; set; }
        public string CurrencyCode { get; set; } = "USD";
        public string Interval { get; set; } = nameof(BillingInterval.Monthly);
        public int IntervalCount { get; set; } = 1;
        public DateTime? FirstBillingDate { get; set; }
        public string? Notes { get; set; }

        public static SubscriptionDraft From(ExistingSubscription s) => new()
        {
            Name = s.Name,
            ExternalId = s.ExternalId,
            ContactId = s.Contact?.ContactId.ToString(),
            StartDate = s.StartDate.ToDateTime(TimeOnly.MinValue),
            EndDate = s.EndDate is { } e ? e.ToDateTime(TimeOnly.MinValue) : null,
            Amount = s.Amount.ToString(CultureInfo.InvariantCulture),
            CurrencyCode = s.CurrencyCode,
            Interval = s.Interval.ToString(),
            IntervalCount = s.IntervalCount,
            FirstBillingDate = s.FirstBillingDate.ToDateTime(TimeOnly.MinValue),
            Notes = s.Notes,
        };
    }
}
