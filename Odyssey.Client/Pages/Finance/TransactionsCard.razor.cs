using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Authorization;

namespace Odyssey.Client.Pages.Finance;

public partial class TransactionsCard
{
    private List<ExistingTransaction> _transactions = new();
    // Server-computed rollup backing the overview breakdown + header count/totals (issue #372). It
    // covers the whole ledger — unlike the display list, which the server filters — and replaces the
    // full-table fetch the page used to do just to count and sum rows in the browser.
    private TransactionSummary? _summary;
    private List<ExistingAccount> _accounts = new();
    private List<ExistingTransactionTag> _tags = new();

    private bool _isLoading = true;
    private bool _refetching;
    private bool _loadError;
    private string _announce = "";

    // Server pagination (OdsPager): 1-based page + rows-per-page. The footer pager owns the size;
    // the toolbar OdsPageSizeSelect mirrors it. TotalCount comes from the server's PagedResult.
    private int _page = 1;
    private int _pageSize = OdsPageSizes.Default[0];
    private int _totalCount;
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;
    private bool _canDownloadFiles;
    private bool _canUploadFiles;
    private bool _canDeleteFiles;

    private const string PageStateKey = "transactions-page";
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _search = string.Empty;
    private IReadOnlyCollection<string> _accountFilter = [];
    private IReadOnlyCollection<string> _statusFilter = [];
    private IReadOnlyCollection<string> _tagFilter = [];
    private IReadOnlyCollection<string> _directionFilter = [];

    // Sort (§6.2): the curated keys already back the OdsTxnTable columns; one OdsTableSort syncs
    // the toolbar control with the header sort. Default: Date, newest first.
    private static readonly OdsTableSort DefaultSort = new("date", OdsSortDirection.Desc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<ExistingTransaction>> _sortFields =
    [
        new() { Key = "date", Label = "Date", Type = OdsSortType.Date },
        new() { Key = "amount", Label = "Amount", Type = OdsSortType.Number },
        new() { Key = "desc", Label = "Description", Type = OdsSortType.Text },
        new() { Key = "contact", Label = "Contact", Type = OdsSortType.Text },
        new() { Key = "account", Label = "Account", Type = OdsSortType.Text },
        new() { Key = "status", Label = "Status", Type = OdsSortType.Status },
    ];

    // OdsMultiSelect filter option projections (the picker keys on string values).
    private IReadOnlyList<OdsOption> _accountOptions =>
        [.. _accounts.Select(account => new OdsOption(account.AccountId.ToString(), account.Name))];
    private IReadOnlyList<OdsOption> _tagOptions =>
        [.. _tags.Select(tag => new OdsOption(tag.TransactionTagId.ToString(), tag.Name))];
    private static readonly IReadOnlyList<OdsOption> _statusOptions =
        [.. Enum.GetValues<TransactionStatus>().Select(status => new OdsOption(status.ToString(), status.ToString()))];

    private IReadOnlyList<OdsBreakdownRow> StatusRows => OdsBreakdown.CountedStatusRows<TransactionStatus>(
        status => _summary?.CountsByStatus is not { } c ? 0 : status switch
        {
            TransactionStatus.Approved => c.Approved,
            TransactionStatus.Flagged => c.Flagged,
            _ => c.New,
        },
        new OdsBreakdownDef<TransactionStatus>(TransactionStatus.New, "New", "info", "fiber_new"),
        new OdsBreakdownDef<TransactionStatus>(TransactionStatus.Approved, "Approved", "income", "check_circle"),
        new OdsBreakdownDef<TransactionStatus>(TransactionStatus.Flagged, "Flagged", "expense", "flag"));

    private IReadOnlyList<OdsBreakdownRow> DirectionRows => OdsBreakdown.CountedStatusRows<string>(
        key => key == "income" ? _summary?.IncomeCount ?? 0 : _summary?.ExpenseCount ?? 0,
        new OdsBreakdownDef<string>("income", "Money in", "income", "arrow_downward"),
        new OdsBreakdownDef<string>("expense", "Money out", "expense", "arrow_upward"));
    private static readonly IReadOnlyList<OdsOption> _directionOptions =
        [new("income", "Money in"), new("expense", "Money out")];

    // Header sub totals — the whole ledger's count and its money in / out. Naive cross-currency sums
    // (the app has no FX on this path), shown with a generic "$" like the Accounts "combined" figure;
    // mirrors the design system's Transactions sub. Computed server-side (issue #372).
    private int TotalCount => _summary?.TotalTransactions ?? 0;
    private decimal TotalIn => _summary?.TotalIn ?? 0m;
    private decimal TotalOut => _summary?.TotalOut ?? 0m;

    private static readonly System.Globalization.NumberFormatInfo GenericMoneyFormat = BuildGenericMoneyFormat();
    private static System.Globalization.NumberFormatInfo BuildGenericMoneyFormat()
    {
        var nf = (System.Globalization.NumberFormatInfo)System.Globalization.CultureInfo.CurrentCulture.NumberFormat.Clone();
        nf.CurrencySymbol = "$";
        nf.CurrencyDecimalDigits = 2;
        nf.CurrencyNegativePattern = 1; // "-$n" — leading minus, no parentheses
        return nf;
    }
    private static string Money(decimal value) => value.ToString("C", GenericMoneyFormat);

    private bool _hasFilters => !string.IsNullOrWhiteSpace(_search)
        || _accountFilter.Count > 0 || _statusFilter.Count > 0
        || _tagFilter.Count > 0 || _directionFilter.Count > 0;

    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await RestorePageStateAsync();
        StateHasChanged();
        await LoadPermissionsAsync();
        await RefreshAsync();
        await LoadOptionsAsync();
    }

    // ── Page-state persistence (search section + filters) ─────────────────────
    // Account/Tag options are data-driven (loaded later) → restored as-is; the
    // static Status/Direction filters are sanitised against their options.
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<TransactionsPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(TransactionsPageState state)
    {
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _search = state.Search ?? string.Empty;
        _accountFilter = state.AccountFilter ?? [];
        _tagFilter = state.TagFilter ?? [];
        _statusFilter = _statusOptions.KnownValues(state.StatusFilter);
        _directionFilter = _directionOptions.KnownValues(state.DirectionFilter);
        _sort = OdsSortHelpers.Resolve(_sortFields, state.SortField, state.SortDirection, DefaultSort);
        _pageSize = OdsPageSizes.Restore(state.PageSize);
    }

    private TransactionsPageState BuildPageState() => new()
    {
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _search,
        AccountFilter = [.. _accountFilter],
        StatusFilter = [.. _statusFilter],
        TagFilter = [.. _tagFilter],
        DirectionFilter = [.. _directionFilter],
        SortField = _sort.Key,
        SortDirection = _sort.Dir,
        PageSize = _pageSize,
    };

    private void PersistPageState() => PageState.QueueSave(PageStateKey, BuildPageState());

    private void OnOverviewToggled(bool open) { _overviewOpen = open; PersistPageState(); }
    private void OnSearchToggled(bool open) { _searchOpen = open; PersistPageState(); }
    private void OnSearchChanged(string value) { _search = value ?? string.Empty; PersistPageState(); }
    private async Task OnAccountFilterChanged(IReadOnlyCollection<string> values) { _accountFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnTagFilterChanged(IReadOnlyCollection<string> values) { _tagFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnDirectionFilterChanged(IReadOnlyCollection<string> values) { _directionFilter = values ?? []; PersistPageState(); await ReloadAsync(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await ReloadAsync(); }

    private sealed class TransactionsPageState
    {
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> AccountFilter { get; set; } = [];
        public List<string> StatusFilter { get; set; } = [];
        public List<string> TagFilter { get; set; } = [];
        public List<string> DirectionFilter { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int PageSize { get; set; } = OdsPageSizes.Default[0];
    }

    // Full refresh: unfiltered header rollup + server-filtered display list. Used on load and after
    // mutations — a create/update/delete moves the totals, so both have to be re-fetched.
    private async Task RefreshAsync()
    {
        await Task.WhenAll(LoadSummary(), ReloadAsync());
    }

    private async Task LoadSummary()
    {
        _summary = await Transactions.GetSummaryAsync();
        StateHasChanged();
    }

    // Reset to the first page, then fetch — for any search / filter / sort / size change (the server
    // list contract). Page navigation calls GetTransactions directly so it keeps the requested page.
    private Task ReloadAsync()
    {
        _page = 1;
        return GetTransactions();
    }

    private Task OnPageChanged(int page)
    {
        _page = page;
        return GetTransactions();
    }

    private Task OnPageSizeChanged(int size)
    {
        _pageSize = size;
        _page = 1;
        PersistPageState();
        return GetTransactions();
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();

        _canCreate = user.HasPermission(PermissionClaims.TransactionsCreate);
        _canUpdate = user.HasPermission(PermissionClaims.TransactionsUpdate);
        _canDelete = user.HasPermission(PermissionClaims.TransactionsDelete);
        _canDownloadFiles = user.HasPermission(PermissionClaims.FilesRead);
        _canUploadFiles = user.HasPermission(PermissionClaims.FilesCreate);
        _canDeleteFiles = user.HasPermission(PermissionClaims.FilesDelete);
    }

    // Server-side fetch (issue #277): search + account/status/tag/direction filters + sort applied by the API.
    private async Task GetTransactions()
    {
        // First load blanks the table for a spinner; every later fetch keeps the rows and shows the bar.
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        // Direction is a two-value toggle (income/expense): filter only when exactly one is selected.
        var result = await Transactions.ListAsync(
            _page, _pageSize,
            search: _search,
            accountIds: _accountFilter,
            statuses: _statusFilter,
            tagIds: _tagFilter,
            direction: _directionFilter,
            sortBy: _sort.Key,
            sortDir: _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc");

        var load = result.PagedOrToast(Snackbar, "transactions");
        if (load.IsSuccess)
        {
            _transactions = [.. load.Items];
            _totalCount = load.TotalCount;
            _loadError = false;
            _announce = _totalCount == 0 ? "No transactions match your filters."
                : $"Showing {OdsPagerMath.FirstShown(_page, _pageSize, _totalCount)}–{OdsPagerMath.LastShown(_page, _pageSize, _totalCount)} of {_totalCount} transaction{(_totalCount == 1 ? "" : "s")}.";
        }
        else
        {
            _loadError = true;
            _announce = "Couldn't load transactions.";
        }

        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    // Account / tag lists feed the header filters. Loaded after the table so the grid paints first;
    // failures are non-fatal.
    private async Task LoadOptionsAsync()
    {
        _accounts = ((await Accounts.ListAllAsync()).ItemsOrToast(Snackbar, "accounts"))
            .OrderBy(a => a.Name).ToList();

        _tags = [.. (await ReferenceData.TransactionTagsAsync()).OrderBy(t => t.Name)];

        StateHasChanged();
    }

    private async Task ClearFilters()
    {
        _search = string.Empty;
        _accountFilter = [];
        _statusFilter = [];
        _tagFilter = [];
        _directionFilter = [];
        PersistPageState();
        await ReloadAsync();
    }

    private IReadOnlyList<OdsMenuItem> BuildActions(ExistingTransaction t, OdsRecordActionContext ctx)
    {
        var items = new List<OdsMenuItem>();

        items.Add(new OdsMenuItem
        {
            Icon = ctx.Expanded ? "close" : "expand_more",
            Label = ctx.Expanded ? "Collapse" : "View details",
            OnClick = EventCallback.Factory.Create(this, ctx.Toggle),
        });

        if (_canUpdate)
            items.Add(new OdsMenuItem { Icon = "edit", Label = "Edit", OnClick = EventCallback.Factory.Create(this, () => EditClicked(t)) });

        // Status transitions (New · Approved · Flagged) — offered only for the states the row isn't
        // already in, gated on the update permission.
        if (_canUpdate)
        {
            var statusItems = new List<OdsMenuItem>();
            if (t.Status != TransactionStatus.Approved)
                statusItems.Add(new OdsMenuItem { Icon = "check_circle", Label = "Approve", OnClick = EventCallback.Factory.Create(this, () => SetStatus(t, TransactionStatus.Approved)) });
            if (t.Status != TransactionStatus.Flagged)
                statusItems.Add(new OdsMenuItem { Icon = "flag", Label = "Flag", OnClick = EventCallback.Factory.Create(this, () => SetStatus(t, TransactionStatus.Flagged)) });
            if (t.Status != TransactionStatus.New)
                statusItems.Add(new OdsMenuItem { Icon = "undo", Label = "Reset to New", OnClick = EventCallback.Factory.Create(this, () => SetStatus(t, TransactionStatus.New)) });

            if (statusItems.Count > 0)
            {
                items.Add(new OdsMenuItem { Divider = true });
                items.AddRange(statusItems);
            }
        }

        items.Add(new OdsMenuItem { Divider = true });
        items.Add(new OdsMenuItem { Icon = "fingerprint", TrailingIcon = "content_copy", Label = "Copy ID", OnClick = EventCallback.Factory.Create(this, () => CopyId(t.TransactionId)) });

        if (_canDelete)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem { Icon = "delete", Label = "Delete", Danger = true, OnClick = EventCallback.Factory.Create(this, ctx.Remove) });
        }

        return items;
    }

    private bool _createOpen;
    private Guid _createKey;

    private void AddClicked()
    {
        if (!_canCreate)
            return;

        _createKey = Guid.NewGuid();
        _createOpen = true;
    }

    private ExistingTransaction? _editTransaction;
    private Guid _editTransactionKey;
    private bool _editTransactionOpen;

    private void EditClicked(ExistingTransaction t)
    {
        if (!_canUpdate)
            return;

        _editTransaction = t;
        _editTransactionKey = Guid.NewGuid();
        _editTransactionOpen = true;
    }

    private async Task HandleDelete(object key)
    {
        if (!_canDelete)
            return;

        var transaction = _transactions.FirstOrDefault(t => t.TransactionId.Equals(key));
        if (transaction is null)
            return;

        if ((await Transactions.DeleteAsync(transaction.TransactionId)).Toast(Snackbar, "Delete failed", "Transaction deleted."))
        {
            // Full refresh, not a local Remove: the delete changes the total, so the pager and the
            // current page have to be re-fetched or the page renders short against a stale count.
            await RefreshAsync();
        }

        StateHasChanged();
    }

    // Change a transaction's status in place — PUT a full patch mirroring the current record with the
    // new status, so the ledger's quick Approve / Flag / Reset actions don't require opening the editor.
    private async Task SetStatus(ExistingTransaction t, TransactionStatus status)
    {
        if (!_canUpdate || t.Status == status)
            return;

        var patch = new NewTransaction
        {
            Description = t.Description,
            Amount = t.Amount,
            TimeStamp = t.TimeStamp,
            AccountId = t.AccountId,
            TransactionTagIds = t.TransactionTags.Select(tag => tag.TransactionTagId).ToList(),
            ContactId = t.ContactId ?? t.Contact?.ContactId,
            CurrencyCode = t.CurrencyCode,
            ExternalId = t.ExternalId,
            InternalId = t.InternalId,
            ExtraData = t.ExtraData,
            Status = status,
            StatusComment = t.StatusComment,
        };

        if ((await Transactions.UpdateAsync(t.TransactionId, patch)).Toast(Snackbar, "Update failed", "Transaction updated."))
            await RefreshAsync();
    }

    private Task CopyId(Guid transactionId) =>
        Clipboard.CopyAsync(transactionId.ToString(), "Transaction ID copied to clipboard.");
}
