using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos;


namespace Odyssey.Client.Pages.Finance;

public partial class ContractsCard
{
    private OdsConfirm _confirm = default!;

    // ── Data ────────────────────────────────────────────────────────────────
    private List<ContractListItem> _contracts = [];
    private readonly Dictionary<Guid, ExistingContract> _details = new();
    private ContractSummary? _summary;

    private IReadOnlyList<OdsOption> _accountOptions = [];
    private IReadOnlyList<OdsOption> _institutionOptions = [];
    private IReadOnlyList<OdsOption> _propertyOptions = [];
    private IReadOnlyDictionary<string, string> _propertyStates = new Dictionary<string, string>();

    private Guid? _flashId;

    // Card-list windowing (OdsInfiniteList): "Load N at a time" batch size.
    private int _batch = OdsPageSizes.Batch[0];

    /// <summary>
    /// The effective "ending soon" window, in days — served on the summary, never held as a constant
    /// here. It is an admin-editable system setting, and a local copy is the client-side duplicate of
    /// a server value CLAUDE.md forbids: lowered, the page would keep flagging contracts the server no
    /// longer counts; raised, it would flag fewer than the header claims.
    ///
    /// <para>
    /// The shipped default stands in only until the first summary lands, so the collapsed headline
    /// never reads "ending soon" against nothing at all during the first paint.
    /// </para>
    /// </summary>
    private int EndingWindowDays => _summary?.EndingWindowDays ?? SystemSettingsDefaults.ContractEndingWindowDays;

    // ── UI state ─────────────────────────────────────────────────────────────
    private bool _isLoading = true;
    private bool _refetching;
    // Latest-request-wins for the list fetch (issue #249): a superseded response touches nothing.
    private readonly ListLoader _listLoader = new();
    private bool _loadError;
    private string _announce = "";
    private Guid? _expandedId;

    // ── Persisted page state ───────────────────────────────────────────────────
    private const string PageStateKey = "contracts-page";
    private bool _problemsOpen = true;
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _searchString = string.Empty;
    private IReadOnlyCollection<string> _typeFilter = [];
    private IReadOnlyCollection<string> _statusFilter = [];

    internal static readonly IReadOnlyList<OdsOption> _statusOptions =
        [.. OdsContractStatus.Order.Select(s =>
        {
            // The overview's "By status" glyph and dot colour, so the filter and the tile agree.
            var meta = OdsContractStatus.Meta(s);
            return new OdsOption(s.ToString(), meta.Label) { Icon = meta.Icon, IconColor = meta.DotColor };
        })];

    // ── Sort (§6.8) — toolbar OdsSortSelect is the sole sort surface (no headers). ──
    private static readonly OdsTableSort DefaultSort = new("name", OdsSortDirection.Asc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<ContractListItem>> _sortFields =
    [
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text, SortValue = c => c.Name.ToLowerInvariant() },
        new() { Key = "startDate", Label = "Start date", Type = OdsSortType.Date, SortValue = c => c.StartDate },
        new() { Key = "endDate", Label = "End date", Type = OdsSortType.Date, SortValue = c => c.EndDate },
        new() { Key = "type", Label = "Type", Type = OdsSortType.Status, SortValue = c => (int)c.Type },
        // The SHARED lifecycle rank, not the enum ordinal (issue #145 §8) — the same one the server
        // orders by. Sorting on the ordinal here would put Draft and Ready, the two earliest states,
        // last: they are appended members, because an ordinal is a wire and persistence contract.
        new() { Key = "status", Label = "Status", Type = OdsSortType.Status, SortValue = c => ContractStatusOrder.Rank(c.Status) },
        // ContractSortBy.ReferenceNumber (issue #181) — appended, ascending by default, nulls last in
        // both directions on the server. Case-folded to match the collation the server sorts under.
        new() { Key = "referenceNumber", Label = "Reference number", Type = OdsSortType.Text, SortValue = c => c.ReferenceNumber?.ToLowerInvariant() },
    ];

    // ── Permissions ────────────────────────────────────────────────────────────
    private bool _canCreate;
    private bool _canUpdate;

    /// <summary>
    /// Whether the Smart tags section may read its match. The scoped endpoint
    /// (<c>GET /api/contracts/{id}/smart-tag-transactions</c>, issue #226) needs
    /// <c>transactions.read</c> as well as <c>contracts.read</c>; without it the section keeps its
    /// chips and names the missing claim instead of issuing a read that would be refused.
    /// </summary>
    private bool _canReadTransactions;
    private bool _canDelete;
    private bool _canDownloadFiles;
    private bool _canViewAccounts;
    private bool _canViewContacts;
    private bool _canViewProperties;

    // ── Computed ────────────────────────────────────────────────────────────────
    // Derived from the unfiltered summary (issue #277): the sub-line reflects the whole set, not the
    // server-filtered display list. Non-archived = total minus archived.
    private int ActiveCount => _summary is { } s ? s.TotalContracts - s.CountsByStatus.Archived : 0;

    // The list is server-filtered, so an empty result only means "first run" when nothing is filtering it.
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_searchString)
        || _typeFilter.Count > 0 || _statusFilter.Count > 0;

    private static DateTime Today => DateTime.UtcNow.Date;


    // ── Lifecycle ────────────────────────────────────────────────────────────────
    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        await RestorePageStateAsync();
        await LoadPermissionsAsync();
        await Task.WhenAll(LoadContracts(), LoadSummary(), LoadAccounts(), LoadInstitutions(), LoadPropertyOptions(), LoadCurrencies());
    }

    // ── Page-state persistence ─────────────────────────────────────────────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<ContractsPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(ContractsPageState state)
    {
        _problemsOpen = state.ProblemsOpen;
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _searchString = state.Search ?? string.Empty;
        _typeFilter = OdsTypeRegistries.ContractOptions.KnownValues(state.TypeFilter);
        _statusFilter = _statusOptions.KnownValues(state.StatusFilter);
        _sort = OdsSortHelpers.Resolve(_sortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private ContractsPageState BuildPageState() => new()
    {
        ProblemsOpen = _problemsOpen,
        OverviewOpen = _overviewOpen,
        SearchOpen = _searchOpen,
        Search = _searchString,
        TypeFilter = [.. _typeFilter],
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
    private async Task OnTypeFilterChanged(IReadOnlyCollection<string> values) { _typeFilter = values ?? []; PersistPageState(); await LoadContracts(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await LoadContracts(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await LoadContracts(); }
    private void OnBatchChanged(int size) { _batch = size; PersistPageState(); StateHasChanged(); }

    private async Task ClearFilters()
    {
        _searchString = string.Empty;
        _typeFilter = [];
        _statusFilter = [];
        PersistPageState();
        await LoadContracts();
    }

    private sealed class ContractsPageState
    {
        public bool ProblemsOpen { get; set; } = true;
        public bool OverviewOpen { get; set; } = true;
        public bool SearchOpen { get; set; } = true;
        public string Search { get; set; } = string.Empty;
        public List<string> TypeFilter { get; set; } = [];
        public List<string> StatusFilter { get; set; } = [];
        public string? SortField { get; set; }
        public OdsSortDirection? SortDirection { get; set; }
        public int BatchSize { get; set; } = OdsPageSizes.Batch[0];
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.ContractsCreate);
        _canUpdate = user.HasPermission(PermissionClaims.ContractsUpdate);
        _canDelete = user.HasPermission(PermissionClaims.ContractsDelete);
        _canDownloadFiles = user.HasPermission(PermissionClaims.FilesRead);
        _canReadTransactions = user.HasPermission(PermissionClaims.TransactionsRead);
        _canViewAccounts = user.HasPermission(PermissionClaims.AccountsRead);
        _canViewContacts = user.HasPermission(PermissionClaims.ContactsRead);
        _canViewProperties = user.HasPermission(PermissionClaims.PropertiesRead);
    }

    // Server-side (issue #277): search + multi type/status filters + sort applied by the API.
    private async Task LoadContracts()
    {
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        // Track failure explicitly: ItemsOrToast falls back to [], which is indistinguishable from a
        // genuinely empty set and would render the onboarding empty state after a 500.
        var response = await _listLoader.RunAsync(ct => Contracts.ListAsync(
            _searchString,
            _typeFilter,
            _statusFilter,
            _sort.Key,
            _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc",
            ct));
        if (response.IsSuperseded)
        {
            return;
        }

        var result = response.Value;

        _contracts = result.ItemsOrToast(Snackbar, "contracts");
        _loadError = !result.IsSuccess;

        _announce = _loadError ? "Couldn't load contracts."
            : _contracts.Count == 0 ? "No contracts match your filters."
            : $"Showing {_contracts.Count} contract{(_contracts.Count == 1 ? "" : "s")}.";
        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    private async Task LoadSummary()
    {
        // The run rate converts into the reader's own display currency, the same source the
        // the summary uses; blank lets the server pick the most common one.
        _summary = await Contracts.GetSummaryAsync(UserPreferences.DefaultCurrency);
        StateHasChanged();
    }

    private async Task LoadAccounts()
    {
        var accounts = (await Accounts.ListAllAsync()).ItemsOrToast(Snackbar, "accounts");
        _accountOptions =
        [
            .. accounts
                .Where(a => a.Archived is null)
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(a => new OdsOption(a.AccountId.ToString(), a.Name)
                {
                    Icon = AccountTypeVisuals.MaterialIcon(a.AccountType),
                    IconColor = AccountTypeVisuals.FgColor(a.AccountType),
                })
        ];
    }

    /// <summary>
    /// The Property party kind's options (issue #208). Unlike accounts, archived and disposed
    /// properties stay in the list — a contract's history must stay recordable — labelled with their
    /// state; only a <c>properties.read</c> holder loads them at all.
    /// </summary>
    private async Task LoadPropertyOptions()
    {
        if (!_canViewProperties)
        {
            return;
        }

        var properties = (await PropertiesApi.ListAllAsync()).ItemsOrToast(Snackbar, "properties");
        _propertyOptions = PropertyVisuals.PartyOptions(properties);
        _propertyStates = PropertyVisuals.PartyLinkStates(properties);
    }

    private async Task LoadInstitutions()
    {
        var contacts = await ReferenceData.ContactsAsync();
        _institutionOptions =
            OdsContactOptions.Active(contacts);
    }

    public void Dispose() => _listLoader.Dispose();
}
