using Odyssey.ApiClient;
using System.Globalization;
using Odyssey.ApiClient.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using Odyssey.Client.Authorization;
using Odyssey.Dtos.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class ContractsCard
{
    // ── Data ────────────────────────────────────────────────────────────────
    private List<ContractListItem> _contracts = [];
    private readonly Dictionary<Guid, ExistingContract> _details = new();
    private ContractSummary? _summary;

    private IReadOnlyList<OdsOption> _accountOptions = [];
    private IReadOnlyList<OdsOption> _institutionOptions = [];
    private IReadOnlyList<OdsOption> _policyOptions = [];

    private Guid? _flashId;

    // Card-list windowing (OdsInfiniteList): "Load N at a time" batch size.
    private int _batch = OdsPageSizes.Batch[0];

    /// <summary>Active contracts whose end date falls within this many days read as "ending soon".</summary>
    private const int EndingWindowDays = 45;

    // ── UI state ─────────────────────────────────────────────────────────────
    private bool _isLoading = true;
    private bool _refetching;
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

    private static readonly IReadOnlyList<OdsOption> _statusOptions =
        [.. OdsContractStatus.Order.Select(s => new OdsOption(s.ToString(), OdsContractStatus.Meta(s).Label))];

    // ── Sort (§6.8) — toolbar OdsSortSelect is the sole sort surface (no headers). ──
    private static readonly OdsTableSort DefaultSort = new("name", OdsSortDirection.Asc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<ContractListItem>> _sortFields =
    [
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text, SortValue = c => c.Name.ToLowerInvariant() },
        new() { Key = "startDate", Label = "Start date", Type = OdsSortType.Date, SortValue = c => c.StartDate },
        new() { Key = "endDate", Label = "End date", Type = OdsSortType.Date, SortValue = c => c.EndDate },
        new() { Key = "type", Label = "Type", Type = OdsSortType.Status, SortValue = c => (int)c.Type },
        new() { Key = "status", Label = "Status", Type = OdsSortType.Status, SortValue = c => (int)c.Status },
    ];

    // ── Permissions ────────────────────────────────────────────────────────────
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;
    private bool _canDownloadFiles;
    private bool _canUploadFiles;

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
            return;

        await RestorePageStateAsync();
        await LoadPermissionsAsync();
        await Task.WhenAll(LoadContracts(), LoadSummary(), LoadAccounts(), LoadInstitutions(), LoadPolicies());
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
        _canUploadFiles = user.HasPermission(PermissionClaims.FilesCreate)
                       && user.HasPermission(PermissionClaims.FilesRead)
                       && user.HasPermission(PermissionClaims.ContractsUpdate);
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
        var result = await Contracts.ListAsync(
            _searchString,
            _typeFilter,
            _statusFilter,
            _sort.Key,
            _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc");

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
        _summary = await Contracts.GetSummaryAsync();
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

    private async Task LoadInstitutions()
    {
        var contacts = await ReferenceData.ContactsAsync();
        _institutionOptions =
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

    private async Task LoadPolicies()
    {
        // Insurance policies are an optional party kind; if the user lacks insurance.read the list
        // comes back empty and the picker simply offers none.
        var policies = (await Insurance.ListAsync()).ItemsOrToast(Snackbar, "insurance policies");
        _policyOptions =
        [
            .. policies
                .Where(p => p.Archived is null)
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(p =>
                {
                    var meta = OdsTypeRegistries.InsurancePolicyTypeOf(p.Type);
                    return new OdsOption(p.InsurancePolicyId.ToString(), p.Name) { Icon = meta.Icon, IconColor = meta.Color };
                })
        ];
    }

    // ── Header problem rollup (active contracts ending soon) ──────────────────────
    private List<PageHeaderProblem> HeaderProblems =>
        _contracts
            .Where(c => c.Archived is null
                && c.Status == ContractStatus.Active
                && c.EndDate is { } end
                && (end.Date - Today).Days <= EndingWindowDays
                && (end.Date - Today).Days >= 0)
            .OrderBy(c => c.EndDate)
            .Select(c => new PageHeaderProblem
            {
                Severity = PageHeaderSeverity.Warning,
                Lead = c.Name,
                Message = $"Term ends {c.EndDate:MMM dd, yyyy}.",
                OnView = EventCallback.Factory.Create(this, () => JumpTo(c.ContractId)),
            })
            .ToList();

    private async Task JumpTo(Guid id)
    {
        await EnsureDetail(id);
        _expandedId = id;
        _flashId = id;
        StateHasChanged();

        try
        {
            await ScrollManager.ScrollIntoViewAsync($"#con-{id}", ScrollBehavior.Smooth);
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

        var contract = await Contracts.GetAsync(id);
        if (contract is not null)
            _details[id] = contract;
        StateHasChanged();
    }

    private async Task ReloadContract(Guid id)
    {
        var contract = await Contracts.GetAsync(id);
        if (contract is not null)
            _details[id] = contract;
        await LoadContracts();
        await LoadSummary();
        StateHasChanged();
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

    private async Task OnContractCreated()
    {
        await LoadContracts();
        await LoadSummary();
    }

    // ── Edit mode (design-system update: the create dialog reused in edit mode, not an inline panel) ──
    private ExistingContract? _editContract;
    private Guid _editContractKey;
    private bool _editContractOpen;

    private async Task EditClicked(ContractListItem c)
    {
        if (!_canUpdate) return;

        await EnsureDetail(c.ContractId);
        if (!_details.TryGetValue(c.ContractId, out var detail))
            return;

        _editContract = detail;
        _editContractKey = Guid.NewGuid();
        _editContractOpen = true;
    }

    private Task OnContractEdited() =>
        _editContract is { } contract ? ReloadContract(contract.ContractId) : Task.CompletedTask;

    // ── Archive / unarchive (PUT with IsArchived; archived contracts stay in the list, dimmed,
    //    hidden by default, and drop out of the active summary counts) ──────────────────────
    private async Task ToggleArchive(ContractListItem c)
    {
        if (!_canUpdate) return;
        await EnsureDetail(c.ContractId);
        if (!_details.TryGetValue(c.ContractId, out var d))
            return;

        var archiving = c.Archived is null;
        var update = new UpdateContract
        {
            Name = d.Name,
            Type = d.Type,
            Description = d.Description,
            StartDate = d.StartDate,
            EndDate = d.EndDate,
            CompletionDate = d.CompletionDate,
            IsArchived = archiving,
        };

        if ((await Contracts.UpdateAsync(c.ContractId, update)).Toast(Snackbar,
                archiving ? "Unable to archive contract" : "Unable to restore contract",
                archiving ? "Contract archived." : "Contract restored."))
        {
            await ReloadContract(c.ContractId);
        }
    }

    // ── Delete (permanent; the API hard-deletes the contract + its party/file links) ──────
    private async Task ConfirmDelete(ContractListItem c)
    {
        if (!_canDelete) return;
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete contract",
            $"Permanently delete '{c.Name}' and all its party and document links? This cannot be undone.",
            yesText: "Delete", cancelText: "Cancel");

        if (confirmed == true && (await Contracts.DeleteAsync(c.ContractId)).Toast(Snackbar, "Delete failed", "Contract deleted."))
        {
            _contracts.Remove(c);
            _details.Remove(c.ContractId);
            if (_expandedId == c.ContractId) _expandedId = null;
            if (_editContract?.ContractId == c.ContractId) { _editContract = null; _editContractOpen = false; }
            await LoadSummary();
            StateHasChanged();
        }
    }

    private Task CopyId(Guid id) => Clipboard.CopyAsync(id.ToString(), "Contract ID copied.");

    // ── Add-party dialog ─────────────────────────────────────────────────────────
    private ExistingContract? _partyContract;
    private Guid _partyKey;
    private bool _partyOpen;

    private async Task AddParty(Guid contractId)
    {
        if (!_canUpdate) return;
        await EnsureDetail(contractId);
        if (!_details.TryGetValue(contractId, out var d)) return;
        _expandedId = contractId;
        _partyContract = d;
        _partyKey = Guid.NewGuid();
        _partyOpen = true;
    }

    // ── Upload / attach dialog ───────────────────────────────────────────────────
    private ExistingContract? _uploadContract;
    private Guid _uploadKey;
    private bool _uploadOpen;

    private async Task AttachDocument(Guid contractId)
    {
        if (!_canUploadFiles) return;
        await EnsureDetail(contractId);
        if (!_details.TryGetValue(contractId, out var d)) return;
        _expandedId = contractId;
        _uploadContract = d;
        _uploadKey = Guid.NewGuid();
        _uploadOpen = true;
    }

    // ── Record-card presentation ──────────────────────────────────────────────────

    /// <summary>The headline figure's colour role. A lapsed term reads expense, one ending inside the
    /// window reads pending; everything else keeps the neutral ink, archived included — a retired
    /// record is not a problem.</summary>
    private static OdsRecordFigureTone HeadlineTone(string cls) => cls switch
    {
        "expired" => OdsRecordFigureTone.Expense,
        "soon" => OdsRecordFigureTone.Pending,
        _ => OdsRecordFigureTone.Neutral,
    };

    /// <summary>The Status tile's value tint, from the same registry the status chip reads, so the
    /// chip in the header and the tile in the body can never disagree.</summary>
    private static OdsInfoTileTone StatusTone(string chipTone) => chipTone switch
    {
        "income" => OdsInfoTileTone.Income,
        "info" => OdsInfoTileTone.Info,
        "expense" => OdsInfoTileTone.Expense,
        _ => OdsInfoTileTone.Muted,
    };

    /// <summary>The date the current state began — a status is a reading of the record at a moment,
    /// so it carries when that moment was. Null where the record has no date to point at.</summary>
    private static string? StatusFoot(ExistingContract c, bool oneOff) => c.Status switch
    {
        ContractStatus.Archived => c.Archived is { } a ? $"since {LongDate(a)}" : null,
        ContractStatus.Expired => c.EndDate is { } e ? $"since {LongDate(e)}" : null,
        ContractStatus.Upcoming => c.StartDate is { } s ? $"starts {LongDate(s)}" : null,
        _ when oneOff => c.CompletionDate is { } d ? $"completed {LongDate(d)}" : null,
        _ => c.StartDate is { } s ? $"since {LongDate(s)}" : null,
    };

    /// <summary>
    /// The row action menu, permission-gated. Archive is offered with its reason rather than hidden
    /// when it does not apply: the lifecycle is ordered (only an ended contract can be archived), and
    /// a disabled item that says why is more use than an item that silently is not there. The server
    /// enforces the same rule.
    ///
    /// <para>
    /// "Ended" is not the derived <see cref="ContractStatus.Expired"/>: a delivered one-off is over
    /// but reads Active, so completion counts too. Same two-branch test the service applies.
    /// </para>
    /// </summary>
    private IReadOnlyList<OdsMenuItem> RowActions(ContractListItem c, bool archived)
    {
        var hasEnded = (c.EndDate is { } end && end.Date < Today)
            || (c.CompletionDate is { } completion && completion.Date <= Today);
        var items = new List<OdsMenuItem>();

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "edit",
                Label = "Edit contract",
                OnClick = EventCallback.Factory.Create(this, () => EditClicked(c)),
            });
            items.Add(new OdsMenuItem
            {
                Icon = "group_add",
                Label = "Add party",
                OnClick = EventCallback.Factory.Create(this, () => AddParty(c.ContractId)),
            });
        }

        if (_canUploadFiles)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "upload_file",
                Label = "Upload document",
                OnClick = EventCallback.Factory.Create(this, () => AttachDocument(c.ContractId)),
            });
        }

        items.Add(new OdsMenuItem
        {
            Icon = "fingerprint",
            Label = "Copy ID",
            TrailingIcon = "content_copy",
            OnClick = EventCallback.Factory.Create(this, () => CopyId(c.ContractId)),
        });

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(hasEnded || archived
                ? new OdsMenuItem
                {
                    Icon = archived ? "unarchive" : "inventory_2",
                    Label = archived ? "Restore" : "Archive",
                    OnClick = EventCallback.Factory.Create(this, () => ToggleArchive(c)),
                }
                : new OdsMenuItem
                {
                    Icon = "inventory_2",
                    Label = "Archive",
                    Disabled = true,
                    Description = "The contract has to end first.",
                });
        }

        if (_canDelete)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "delete",
                Label = "Delete",
                Danger = true,
                OnClick = EventCallback.Factory.Create(this, () => ConfirmDelete(c)),
            });
        }

        return items;
    }

    /// <summary>An optional tile foot. Returns null for an absent caption so the tile renders no foot
    /// element at all — a foot has to earn its place, and an empty one is not the same as none.</summary>
    private static RenderFragment? Caption(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : builder => builder.AddContent(0, text);

    private static string LongDate(DateTime date) => date.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    // ── Collapsed headline figure (mirrors the design's conHeadline) ──────────────
    private (bool HasValue, string Value, string Word, string Cls) Headline(ContractListItem c)
    {
        // One-off contracts headline on their completion date (no ongoing term).
        if (c.CompletionDate is { } completion)
        {
            if (c.Status == ContractStatus.Archived)
                return (true, completion.ToString("MMM dd, yyyy"), "archived", "archived");
            var toGo = (completion.Date - Today).Days;
            var oneOffWord = toGo > 0
                ? $"completes in {toGo} day{(toGo == 1 ? "" : "s")}"
                : "one-off";
            return (true, completion.ToString("MMM dd, yyyy"), oneOffWord, "");
        }

        switch (c.Status)
        {
            case ContractStatus.Upcoming when c.StartDate is { } start:
            {
                var days = (start.Date - Today).Days;
                var word = days <= 0 ? "starts today" : $"starts in {days} day{(days == 1 ? "" : "s")}";
                return (true, start.ToString("MMM dd, yyyy"), word, "");
            }
            case ContractStatus.Expired when c.EndDate is { } end:
            {
                var days = (Today - end.Date).Days;
                var word = days <= 0 ? "expired today" : $"expired {days} day{(days == 1 ? "" : "s")} ago";
                return (true, end.ToString("MMM dd, yyyy"), word, "expired");
            }
            case ContractStatus.Archived:
            {
                var anchor = c.EndDate ?? c.StartDate;
                return anchor is { } a
                    ? (true, a.ToString("MMM dd, yyyy"), "archived", "archived")
                    : (false, "Archived", "archived", "archived");
            }
            default: // Active
                if (c.EndDate is { } activeEnd)
                {
                    var days = (activeEnd.Date - Today).Days;
                    var soon = days <= EndingWindowDays;
                    var word = days <= 0 ? "ends today" : $"ends in {days} day{(days == 1 ? "" : "s")}";
                    return (true, activeEnd.ToString("MMM dd, yyyy"), word, soon ? "soon" : "");
                }
                return (false, "Open-ended", "no end date", "");
        }
    }
}
