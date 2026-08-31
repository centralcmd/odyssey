using Odyssey.ApiClient;
using Odyssey.ApiClient.Resources;
using Microsoft.AspNetCore.Components;
using System.Net.Http.Json;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.Client.Auth;
using Odyssey.Client.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Models;
using Odyssey.Client.Services;
using Odyssey.Client.Theme;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class BudgetsCard
{
    private List<ExistingBudget> _budgets = [];
    // Server-computed rollup backing the header count + planned balance (issue #372) — it spans every
    // budget, unlike the server-filtered display list, without fetching one.
    private BudgetSummary? _summary;
    private List<ExistingCurrency> _currencies = [];
    private Dictionary<string, ExistingCurrency> _currenciesByCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NumberFormatInfo> _moneyFormatCache = new(StringComparer.OrdinalIgnoreCase);
    private List<ExistingTransactionTag> _transactionTags = [];

    // Lazily-loaded per-budget reports (transactions + per-tag actual sums).
    private readonly Dictionary<Guid, BudgetReport> _reports = new();
    private readonly HashSet<Guid> _loadingReports = new();

    private bool _isLoading = true;
    private bool _refetching;
    private bool _loadError;
    private string _announce = "";
    private Guid? _expandedId;

    // "Edit multiple" moved out of the items section's own header and onto the row menu (a section
    // header inside a record body labels, it does not act), so the page owns the mode. At most one
    // budget is in it — the same budget as the open card.
    private Guid? _editingItemsId;

    // Card-list windowing (OdsInfiniteList): "Load N at a time" batch size.
    private int _batch = OdsPageSizes.Batch[0];

    private const string PageStateKey = "budgets-page";
    private bool _searchOpen = true;
    private string _searchString = string.Empty;
    private IReadOnlyCollection<string> _statusFilter = [];

    // The list is server-filtered, so an empty result only means "first run" when nothing is filtering it.
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_searchString) || _statusFilter.Count > 0;

    private static readonly IReadOnlyList<OdsOption> _statusOptions =
        [new("Active", "Active"), new("Archived", "Archived")];

    // ── Sort (§6.3) — toolbar OdsSortSelect is the sole sort surface (no headers). ──
    private static readonly OdsTableSort DefaultSort = new("startDate", OdsSortDirection.Desc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<ExistingBudget>> _sortFields =
    [
        new() { Key = "startDate", Label = "Start date", Type = OdsSortType.Date, SortValue = b => b.StartDate },
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text, SortValue = b => b.Name.ToLowerInvariant() },
        new() { Key = "endDate", Label = "End date", Type = OdsSortType.Date, SortValue = b => b.EndDate },
    ];

    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;
    private bool _canReadTransactions;
    private bool _canDownloadFiles;

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await RestorePageStateAsync();
        StateHasChanged();
        await LoadPermissionsAsync();
        await Task.WhenAll(GetBudgets(), LoadSummary(), LoadCurrencies(), LoadTransactionTags());
    }

    // ── Page-state persistence (Overview + Search sections, filters) ──────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<BudgetsPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(BudgetsPageState state)
    {
        _searchOpen   = state.SearchOpen;
        _searchString = state.Search ?? string.Empty;
        _statusFilter = _statusOptions.KnownValues(state.StatusFilter);
        _sort         = OdsSortHelpers.Resolve(_sortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch        = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private BudgetsPageState BuildPageState() => new()
    {
        SearchOpen    = _searchOpen,
        Search        = _searchString,
        StatusFilter  = [.. _statusFilter],
        SortField     = _sort.Key,
        SortDirection = _sort.Dir,
        BatchSize     = _batch,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _searchString = value ?? string.Empty; PersistPageState(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await GetBudgets(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await GetBudgets(); }
    private void OnBatchChanged(int size) { _batch = size; PersistPageState(); StateHasChanged(); }

    private async Task ClearFilters()
    {
        _searchString = string.Empty;
        _statusFilter = [];
        PersistPageState();
        await GetBudgets();
    }

    private sealed class BudgetsPageState
    {
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> StatusFilter { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int BatchSize { get; set; } = OdsPageSizes.Batch[0];
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.BudgetsCreate);
        _canUpdate = user.HasPermission(PermissionClaims.BudgetsUpdate);
        _canDelete = user.HasPermission(PermissionClaims.BudgetsDelete);
        _canReadTransactions = user.HasPermission(PermissionClaims.TransactionsRead);
        _canDownloadFiles = user.HasPermission(PermissionClaims.FilesRead);
    }

    // Server-side fetch (issue #277): name search + status filter + sort applied by the API.
    private async Task GetBudgets()
    {
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        var result = await Budgets.ListAsync(
            page: 1, pageSize: PagedQuery.SizeAll,
            search: _searchString,
            status: _statusFilter,
            sortBy: _sort.Key,
            sortDir: _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc");

        var load = result.PagedOrToast(Snackbar, "budgets");
        if (load.IsSuccess)
        {
            _budgets = [.. load.Items];
            _loadError = false;
            _announce = _budgets.Count == 0 ? "No budgets match your filters."
                : $"Showing {_budgets.Count} budget{(_budgets.Count == 1 ? "" : "s")}.";
        }
        else
        {
            _loadError = true;
            _announce = "Couldn't load budgets.";
        }

        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    // Unfiltered rollup for the header count + planned-balance sub (issue #372).
    private async Task LoadSummary()
    {
        _summary = await Budgets.GetSummaryAsync();
        StateHasChanged();
    }

    // A create/edit/archive/delete can change the header totals → refresh both the rollup + display.
    private async Task OnBudgetChanged()
    {
        await LoadSummary();
        await GetBudgets();
    }

    private async Task LoadCurrencies()
    {
        _currencies = [.. (await ReferenceData.CurrenciesAsync()).Where(c => c.Archived is null)];
        _currenciesByCode = _currencies.ToDictionary(c => c.CurrencyCode, StringComparer.OrdinalIgnoreCase);
        _moneyFormatCache.Clear(); // currency symbols/minor-units may have changed
    }

    private async Task LoadTransactionTags()
    {
        _transactionTags = [.. await ReferenceData.TransactionTagsAsync()];
    }

    private async Task ToggleExpand(Guid budgetId)
    {
        // Batch editing only makes sense while the record is open, and only one card is open at a
        // time — so closing (or opening another) leaves the mode behind.
        _editingItemsId = null;

        if (_expandedId == budgetId)
        {
            _expandedId = null;
            return;
        }

        _expandedId = budgetId;
        if (!_reports.ContainsKey(budgetId))
            await LoadReport(budgetId);
    }

    /// <summary>
    /// Enter / leave the items batch grid for one budget, opening the record if needed.
    ///
    /// <para>
    /// The toggle lives on the row menu, which <see cref="OdsRecordCard"/> renders whether or not the
    /// card is open — so this can fire on a COLLAPSED row, expanding it and swapping a read-only table
    /// for a multi-row edit grid several sections further down the page. That is a significant content
    /// change the user did not visibly navigate to, so it is announced through the page's live region
    /// (WCAG 2.2 §4.1.3 Status Messages). The announcement names the expansion only when one actually
    /// happened; re-announcing it for an already-open card would be noise.
    /// </para>
    /// </summary>
    private async Task SetItemsEditing(Guid budgetId, bool editing)
    {
        var expanding = editing && _expandedId != budgetId;
        if (expanding)
            await ToggleExpand(budgetId);

        _editingItemsId = editing ? budgetId : null;

        var name = _budgets.FirstOrDefault(b => b.BudgetId == budgetId)?.Name;
        _announce = editing
            ? expanding ? $"{name} expanded. Editing budget items." : "Editing budget items."
            : "Stopped editing budget items.";

        StateHasChanged();
    }

    private async Task LoadReport(Guid budgetId)
    {
        _loadingReports.Add(budgetId);
        var report = (await Budgets.GetReportAsync(budgetId)).OrToast(Snackbar, "Unable to load budget transactions");
        if (report is not null)
            _reports[budgetId] = report;
        _loadingReports.Remove(budgetId);
        StateHasChanged();
    }

    // Reload a single budget (items changed) and its report.
    private async Task OnBudgetChanged(Guid budgetId)
    {
        var budget = (await Budgets.GetAsync(budgetId)).OrToast(Snackbar, "Unable to load the budget");
        if (budget is not null)
        {
            var index = _budgets.FindIndex(b => b.BudgetId == budgetId);
            if (index >= 0)
                _budgets[index] = budget;
        }

        await LoadReport(budgetId);
    }

    // ── Computed ─────────────────────────────────────────────────────────
    // Header count + planned-balance sub come from the server rollup (issue #372): the live budgets
    // across the whole set, not the filtered display list.
    private int ActiveCount => _summary?.ActiveCount ?? 0;
    private decimal PlannedBalance => _summary?.PlannedBalance ?? 0m;

    // Planned amounts by item name within a single budget, for that budget's detail donuts.
    private static List<KeyValuePair<string, decimal>> BudgetItemSlices(ExistingBudget budget, BudgetCategoryType category) =>
        budget.BudgetItems
            .Where(i => i.CategoryType == category && i.PlannedAmount > 0)
            .GroupBy(i => i.Name)
            .Select(g => new KeyValuePair<string, decimal>(g.Key, g.Sum(i => i.PlannedAmount)))
            .OrderByDescending(kv => kv.Value)
            .ToList();

    // Per-budget signed actual sums keyed by transaction-tag id, derived from its report.
    private Dictionary<Guid, decimal> ActualByTag(Guid budgetId)
    {
        var result = new Dictionary<Guid, decimal>();
        if (!_reports.TryGetValue(budgetId, out var report))
            return result;

        foreach (var summary in report.ExistingTransactionReport)
        {
            var tagId = summary.ExistingTransactionTag.TransactionTagId;
            result[tagId] = result.GetValueOrDefault(tagId) + summary.Sum;
        }
        return result;
    }

    private static decimal ActualIncome(ExistingBudget budget, Dictionary<Guid, decimal> actualByTag) =>
        budget.BudgetItems
            .Where(i => i.CategoryType == BudgetCategoryType.Income && i.TransactionTagId is not null)
            .Sum(i => actualByTag.GetValueOrDefault(i.TransactionTagId!.Value));

    private static decimal ActualExpenses(ExistingBudget budget, Dictionary<Guid, decimal> actualByTag) =>
        budget.BudgetItems
            .Where(i => i.CategoryType == BudgetCategoryType.Expense && i.TransactionTagId is not null)
            .Sum(i => Math.Abs(actualByTag.GetValueOrDefault(i.TransactionTagId!.Value)));

    // Distinct transaction-tag ids referenced by a budget's items, used to fetch its transactions.
    private static List<Guid> BudgetTagIds(ExistingBudget budget) =>
        budget.BudgetItems
            .Where(i => i.TransactionTagId is not null)
            .Select(i => i.TransactionTagId!.Value)
            .Distinct()
            .ToList();

    // ── CRUD ─────────────────────────────────────────────────────────────
    private bool _createBudgetOpen;
    private Guid _createBudgetKey;

    private void AddClicked()
    {
        if (!_canCreate)
            return;

        _createBudgetKey = Guid.NewGuid();
        _createBudgetOpen = true;
    }

    // ── Edit mode (design-system update: the create dialog reused in edit mode, not an inline panel) ──
    private ExistingBudget? _editBudget;
    private Guid _editBudgetKey;
    private bool _editBudgetOpen;

    private void EditClicked(ExistingBudget budget)
    {
        if (!_canUpdate)
            return;

        _editBudget = budget;
        _editBudgetKey = Guid.NewGuid();
        _editBudgetOpen = true;
    }

    private async Task OnBudgetEdited()
    {
        await OnBudgetChanged();

        // Dates and base currency drive the transaction report — drop the cached one if it's loaded.
        if (_editBudget is not null && _reports.ContainsKey(_editBudget.BudgetId))
            await LoadReport(_editBudget.BudgetId);
    }

    private ExistingBudget? _itemBudget;
    private List<Guid> _itemUsedTagIds = [];
    private Guid _itemKey;
    private bool _itemOpen;

    private void AddItemClicked(ExistingBudget budget)
    {
        if (!_canCreate)
            return;

        _itemUsedTagIds = budget.BudgetItems
            .Where(i => i.TransactionTagId.HasValue)
            .Select(i => i.TransactionTagId!.Value)
            .Distinct()
            .ToList();
        _itemBudget = budget;
        _itemKey = Guid.NewGuid();
        _itemOpen = true;
        _expandedId = budget.BudgetId;
    }

    private async Task OnItemSaved()
    {
        if (_itemBudget is null)
            return;

        // Reveal the budget so the new item is visible, then refresh it.
        _expandedId = _itemBudget.BudgetId;
        await OnBudgetChanged(_itemBudget.BudgetId);
    }

    private async Task ToggleArchive(ExistingBudget budget)
    {
        if (!_canUpdate)
            return;

        var targetArchived = budget.Archived is null;
        var update = new NewBudget
        {
            Name = budget.Name,
            Description = budget.Description,
            StartDate = budget.StartDate,
            EndDate = budget.EndDate,
            BaseCurrencyCode = budget.BaseCurrencyCode,
            Archived = targetArchived,
        };

        if ((await Budgets.UpdateAsync(budget.BudgetId, update)).Toast(Snackbar, "Update failed",
                targetArchived ? "Budget archived." : "Budget unarchived."))
        {
            budget.Archived = targetArchived ? DateTime.UtcNow : null;
            await LoadSummary();
            StateHasChanged();
        }
    }

    private async Task ConfirmDelete(ExistingBudget budget)
    {
        if (!_canDelete)
            return;

        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete budget",
            $"Delete '{budget.Name}'? This cannot be undone.",
            yesText: "Delete", cancelText: "Cancel");
        if (confirmed == true)
            await DeleteBudget(budget);
    }

    private async Task DeleteBudget(ExistingBudget budget)
    {
        if ((await Budgets.DeleteAsync(budget.BudgetId)).Toast(Snackbar, "Delete failed", "Budget deleted."))
        {
            _budgets.Remove(budget);
            _reports.Remove(budget.BudgetId);
            await LoadSummary();
            if (_expandedId == budget.BudgetId)
                _expandedId = null;
        }
    }

    private Task CopyBudgetId(Guid budgetId) =>
        Clipboard.CopyAsync(budgetId.ToString(), "Budget ID copied.");

    // ── Row actions ──────────────────────────────────────────────────────
    /// <summary>The row action menu. Every section-level action lives here — a section header inside a
    /// record body labels, it does not act — so "New item" and "Edit multiple" are the budget-items
    /// section's only entry points.</summary>
    private IReadOnlyList<OdsMenuItem> RowActions(ExistingBudget budget, bool archived, bool editingItems)
    {
        var items = new List<OdsMenuItem>();

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "edit",
                Label = "Edit budget",
                OnClick = EventCallback.Factory.Create(this, () => EditClicked(budget)),
            });
        }

        if (_canCreate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "playlist_add",
                Label = "New item",
                OnClick = EventCallback.Factory.Create(this, () => AddItemClicked(budget)),
            });
        }

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = editingItems ? "check" : "edit_note",
                Label = editingItems ? "Done editing items" : "Edit multiple",
                OnClick = EventCallback.Factory.Create(this,
                    () => SetItemsEditing(budget.BudgetId, !editingItems)),
            });
        }

        items.Add(new OdsMenuItem
        {
            Icon = "fingerprint",
            Label = "Copy ID",
            TrailingIcon = "content_copy",
            OnClick = EventCallback.Factory.Create(this, () => CopyBudgetId(budget.BudgetId)),
        });

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem
            {
                Icon = archived ? "unarchive" : "archive",
                Label = archived ? "Unarchive" : "Archive",
                OnClick = EventCallback.Factory.Create(this, () => ToggleArchive(budget)),
            });
        }

        if (_canDelete)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "delete",
                Label = "Delete",
                Danger = true,
                OnClick = EventCallback.Factory.Create(this, () => ConfirmDelete(budget)),
            });
        }

        return items;
    }

    // ── Display helpers ──────────────────────────────────────────────────
    private static string BalanceColor(decimal value) => value switch
    {
        > 0 => "var(--finance-income)",
        < 0 => "var(--finance-expense)",
        _ => "var(--mud-palette-text-secondary)",
    };

    // The headline figure and the balance tiles take the finance vocabulary, never the record's accent.
    // The rule itself (and why it treats zero differently from BalanceColor above) lives in
    // BudgetBalanceVisuals, where it is testable.
    private static OdsRecordFigureTone BalanceFigureTone(decimal value) => BudgetBalanceVisuals.FigureTone(value);

    private static OdsInfoTileTone BalanceTileTone(decimal value) => BudgetBalanceVisuals.TileTone(value);

    private static string Lines(int count, string noun) => BudgetBalanceVisuals.Lines(count, noun);

    private static string LongDate(DateTime date) => date.ToString("MMM dd, yyyy", CultureInfo.CurrentCulture);

    /// <summary>An optional tile foot. Returns null for an absent caption so the tile renders no foot
    /// element at all — a foot has to earn its place, and an empty one is not the same as none.</summary>
    private static RenderFragment? Caption(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : builder => builder.AddContent(0, text);

    private string FormatMoney(decimal value, string? currencyCode) =>
        value.ToString("C", MoneyFormat(currencyCode));

    // One configured NumberFormatInfo per currency code (and one for the generic
    // fallback), cached so list re-renders don't clone+configure a fresh one per row.
    private NumberFormatInfo MoneyFormat(string? currencyCode)
    {
        var key = string.IsNullOrWhiteSpace(currencyCode) ? string.Empty : currencyCode;
        if (_moneyFormatCache.TryGetValue(key, out var cached))
            return cached;

        var nf = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
        if (key.Length > 0 && _currenciesByCode.TryGetValue(key, out var currency))
        {
            nf.CurrencySymbol = currency.Symbol;
            nf.CurrencyDecimalDigits = currency.MinorUnits;
        }
        else
        {
            nf.CurrencySymbol = "$";
            nf.CurrencyDecimalDigits = 2;
        }
        nf.CurrencyNegativePattern = 1; // "-$n" — leading minus, no parentheses
        _moneyFormatCache[key] = nf;
        return nf;
    }

    // ── Per-budget detail donuts ─────────────────────────────────────────
    // One slice per item name, sized by planned amount — OdsDonut owns the ring
    // geometry, gaps, and categorical --chart-* coloring.
    private static List<OdsDonutSlice> BuildSlices(List<KeyValuePair<string, decimal>> entries) =>
        entries.Select(e => new OdsDonutSlice { Label = e.Key, Value = e.Value }).ToList();
}
