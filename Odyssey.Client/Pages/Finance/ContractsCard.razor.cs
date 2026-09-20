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
using Odyssey.Dtos;

namespace Odyssey.Client.Pages.Finance;

public partial class ContractsCard
{
    // ── Data ────────────────────────────────────────────────────────────────
    private List<ContractListItem> _contracts = [];
    private readonly Dictionary<Guid, ExistingContract> _details = new();
    private ContractSummary? _summary;

    private IReadOnlyList<OdsOption> _accountOptions = [];
    private IReadOnlyList<OdsOption> _institutionOptions = [];

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
        // The SHARED lifecycle rank, not the enum ordinal (issue #145 §8) — the same one the server
        // orders by. Sorting on the ordinal here would put Draft and Ready, the two earliest states,
        // last: they are appended members, because an ordinal is a wire and persistence contract.
        new() { Key = "status", Label = "Status", Type = OdsSortType.Status, SortValue = c => ContractStatusOrder.Rank(c.Status) },
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
        await Task.WhenAll(LoadContracts(), LoadSummary(), LoadAccounts(), LoadInstitutions(), LoadCurrencies());
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
        // The run rate converts into the reader's own display currency, the same source the
        // Subscriptions summary uses; blank lets the server pick the most common one.
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

    private async Task LoadInstitutions()
    {
        var contacts = await ReferenceData.ContactsAsync();
        _institutionOptions =
            OdsContactOptions.Active(contacts);
    }

    // ── Money (term values) ──────────────────────────────────────────────────────
    //
    // A contract has no currency of its own — every term names the one it is priced in — so the
    // formatter is resolved PER VALUE rather than once per record. The format itself comes from
    // DashboardFigures, the one helper that decides a symbol's fallback: a known code with no usable
    // symbol falls back to the CODE, never to "$", since a wrong sigil misreports the denomination.

    private IReadOnlyDictionary<string, ExistingCurrency> _currenciesByCode =
        new Dictionary<string, ExistingCurrency>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, NumberFormatInfo> _moneyFormatCache = new(StringComparer.OrdinalIgnoreCase);

    private async Task LoadCurrencies()
    {
        var currencies = await ReferenceData.CurrenciesAsync();
        _currenciesByCode = currencies.ToDictionary(c => c.CurrencyCode, c => c, StringComparer.OrdinalIgnoreCase);
        _moneyFormatCache.Clear();
    }

    private string FormatMoney(decimal value, string? currencyCode) =>
        value.ToString("C", MoneyFormat(currencyCode));

    // Cached per code so a list re-render does not clone and configure a fresh format per row.
    private NumberFormatInfo MoneyFormat(string? currencyCode)
    {
        var key = string.IsNullOrWhiteSpace(currencyCode) ? string.Empty : currencyCode;
        if (_moneyFormatCache.TryGetValue(key, out var cached))
            return cached;

        _currenciesByCode.TryGetValue(key, out var currency);
        var format = DashboardFigures.MoneyFormat(key.Length == 0 ? null : key, currency);
        _moneyFormatCache[key] = format;
        return format;
    }

    // ── Header signal: "Upcoming" ─────────────────────────────────────────────────
    //
    // Four groups under one button, one count and one worst-severity reading, because they answer one
    // question — what is coming? — and a reader checking for a renewal cliff is checking for a charge
    // in the same breath.
    //
    //   Recently expired  a term that ran out and was never archived: the decision still outstanding
    //   Ending soon       the cliff itself
    //   Starting soon     its mirror — signed, not yet in force
    //   Next charges      what falls due, derived from the fee terms in force (server-computed)
    //
    // EVERY group is read against the loaded LIST, which is server-filtered: narrowing the search
    // narrows the panel with it. That is pre-existing behaviour for the ending-soon group and is kept
    // deliberately — every row carries a jump action, and a row that jumps to a record the list is not
    // showing would scroll to nothing and look broken. The charge rows are computed server-side from
    // term data the list projection does not carry, so they arrive on the unfiltered summary and are
    // intersected back against the list here rather than being exempted from the rule.
    //
    // Two of the three dated groups are capped; ENDING SOON deliberately is not. It is the renewal
    // cliff the whole feature exists to surface, so dropping its seventh row — and under-counting the
    // badge — would hide exactly what a reader opened the panel for. The design system caps the same
    // two and leaves this one unbounded for the same reason.
    private const int MaxDatedSignalRows = 6;

    private List<PageHeaderProblem> HeaderProblems
    {
        get
        {
            var problems = new List<PageHeaderProblem>();
            var live = _contracts.Where(c => c.Archived is null).ToList();

            problems.AddRange(live
                .Where(c => c.Status == ContractStatus.Expired
                    && c.EndDate is { } end && DaysSince(end) >= 0 && DaysSince(end) <= EndingWindowDays)
                .OrderByDescending(c => c.EndDate)
                .Take(MaxDatedSignalRows)
                .Select(c => Dated(c, "Recently expired", PageHeaderSeverity.Error,
                    $"Term expired {OdsRelativeDay.Ago(DaysSince(c.EndDate!.Value))}.")));

            problems.AddRange(live
                .Where(c => c.Status == ContractStatus.Active
                    && c.EndDate is { } end && DaysUntil(end) >= 0 && DaysUntil(end) <= EndingWindowDays)
                .OrderBy(c => c.EndDate)
                .Select(c => Dated(c, "Ending soon", PageHeaderSeverity.Warning,
                    $"Term ends {c.EndDate:MMM dd, yyyy}.")));

            problems.AddRange(live
                .Where(c => c.Status == ContractStatus.Upcoming
                    && c.StartDate is { } start && DaysUntil(start) >= 0 && DaysUntil(start) <= EndingWindowDays)
                .OrderBy(c => c.StartDate)
                .Take(MaxDatedSignalRows)
                .Select(c => Dated(c, "Starting soon", PageHeaderSeverity.Information,
                    $"Term starts {OdsRelativeDay.Ahead(DaysUntil(c.StartDate!.Value))}.")));

            // Awaiting signature: sent out, never returned (issue #145). Not a dated cliff — what
            // makes it actionable is that nothing will move it on its own, and every day it sits
            // there is a day an agreement everyone believes is in force is not. WARNING, unlike the
            // paused group below: a pause is a deliberate state someone chose, while an unreturned
            // signature is a thing that has stalled.
            //
            // DRAFTS ARE DELIBERATELY NOT HERE. A draft is work in progress — nobody is waiting on
            // anyone — and listing every one would turn this panel into a second contract list. The
            // status filter is where you go looking for those.
            problems.AddRange(live
                .Where(c => c.Status == ContractStatus.Ready && c.Ready is not null)
                .OrderBy(c => c.Ready)
                .Take(MaxDatedSignalRows)
                .Select(c => Dated(c, "Awaiting signature", PageHeaderSeverity.Warning,
                    $"Ready for signature {OdsRelativeDay.Ago(DaysSince(c.Ready!.Value))} — not signed, not counted in the run rate.")));

            // Paused agreements: not a cliff, but the one group here that cannot be seen by looking
            // at a date — a contract that stopped costing money because someone froze it, and which
            // nothing will un-freeze on its own. Information, not a warning: a deliberate state is
            // not a fault, and raising it would cry wolf on every contract someone meant to suspend.
            problems.AddRange(live
                .Where(c => c.Status == ContractStatus.Paused && c.Paused is not null)
                .OrderByDescending(c => c.Paused)
                .Take(MaxDatedSignalRows)
                .Select(c => Dated(c, "Paused", PageHeaderSeverity.Information,
                    $"Paused {OdsRelativeDay.Ago(DaysSince(c.Paused!.Value))} — not counted in the run rate.")));

            // Intersected against the list for the reason stated above: the summary is unfiltered, so
            // without this a charge row could name a contract the active filter has excluded, and its
            // jump would scroll to an element that is not on the page — silently, since JumpTo's
            // best-effort scroll swallows the miss.
            var listed = live.Select(c => c.ContractId).ToHashSet();
            problems.AddRange((_summary?.UpcomingCharges ?? [])
                .Where(charge => listed.Contains(charge.ContractId))
                .Select(charge => new PageHeaderProblem
                {
                    Group = "Next charges",
                    // Information: a charge falling due as agreed is not a problem, and letting one
                    // raise the button above info would cry wolf on every contract that has a price.
                    Severity = PageHeaderSeverity.Information,
                    Message = charge.Name,
                    Row = ChargeRow(charge),
                }));

            return problems;
        }
    }

    private PageHeaderProblem Dated(
        ContractListItem contract, string group, PageHeaderSeverity severity, string message) => new()
    {
        Group = group,
        Severity = severity,
        Lead = contract.Name,
        Message = message,
        OnView = EventCallback.Factory.Create(this, () => JumpTo(contract.ContractId)),
    };

    private int DaysUntil(DateTime date) => (date.Date - Today).Days;

    private int DaysSince(DateTime date) => (Today - date.Date).Days;



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

    /// <summary>
    /// Patches one contract's documents in place after a document METADATA edit (issue #146). Such an
    /// edit creates and removes no row, so the list row's file count and every summary figure are
    /// unchanged — pulling the contract, the list and the summary back down for it would be three
    /// requests to observe a one-row change the list endpoint already returned.
    /// </summary>
    private void ApplyContractFiles(Guid id, List<ExistingContractFile> files)
    {
        if (_details.TryGetValue(id, out var contract))
            contract.Files = files;
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
            // PUT is a full replacement, so an omitted flag RESUMES: archiving without carrying the
            // pause stamp forward would silently clear it. The two stamps are orthogonal in storage
            // and only ordered in presentation — archiving a paused contract keeps both.
            IsPaused = d.Paused is not null,
            // Same rule, and the one this action would otherwise BREAK (issue #145 §5.2): omitting
            // these clears both signature stamps, flips a signed contract to Draft and drops it out
            // of the run rate — for a reader who only clicked Archive.
            Ready = d.Ready,
            Signed = d.Signed,
        };

        if ((await Contracts.UpdateAsync(c.ContractId, update)).Toast(Snackbar,
                archiving ? "Unable to archive contract" : "Unable to restore contract",
                archiving ? "Contract archived." : "Contract restored."))
        {
            await ReloadContract(c.ContractId);
        }
    }

    // ── Pause / resume (PUT with IsPaused; a paused contract stays listed at full brightness, stays
    //    fully editable and keeps its price history — it only leaves the money: the run rate, its
    //    by-type split and the next charges) ──────────────────────────────────────────────────────
    private async Task TogglePause(ContractListItem c)
    {
        if (!_canUpdate) return;
        await EnsureDetail(c.ContractId);
        if (!_details.TryGetValue(c.ContractId, out var d))
            return;

        var pausing = d.Paused is null;
        var update = new UpdateContract
        {
            Name = d.Name,
            Type = d.Type,
            Description = d.Description,
            StartDate = d.StartDate,
            EndDate = d.EndDate,
            CompletionDate = d.CompletionDate,
            // Carried forward for the same reason archiving carries the pause stamp: a full
            // replacement that omitted this would restore a contract the reader only meant to resume.
            IsArchived = d.Archived is not null,
            IsPaused = pausing,
            // And the signature stamps for the same reason again (issue #145 §5.2): a pause that
            // cleared them would unsign the contract the reader only meant to suspend.
            Ready = d.Ready,
            Signed = d.Signed,
        };

        if ((await Contracts.UpdateAsync(c.ContractId, update)).Toast(Snackbar,
                pausing ? "Unable to pause contract" : "Unable to resume contract",
                pausing ? "Contract paused." : "Contract resumed."))
        {
            await ReloadContract(c.ContractId);
        }
    }

    // ── Signature lifecycle (issue #145) ─────────────────────────────────────────────────
    //
    // The one-click path, in lifecycle order and one step at a time: Mark ready for signature →
    // Mark signed → Unsign. All three ride the SAME full-replacement PUT the archive and pause
    // actions do; the date fields in the create/edit dialog are the other way in, for backdating a
    // paper contract signed last month.

    /// <summary>
    /// Marks the contract ready for signature. IDEMPOTENT the way a pause is: a repeated mark keeps
    /// the ORIGINAL stamp, so "ready since" never resets — which matters because that date is what
    /// the header's "Awaiting signature" row counts from.
    /// </summary>
    private Task MarkReady(ContractListItem c) =>
        WriteSignature(
            c,
            (d, now) => (d.Ready ?? now, d.Signed),
            "Unable to mark the contract ready", "Contract marked ready for signature.");

    /// <summary>
    /// Marks the contract signed by all parties.
    ///
    /// <para>
    /// It stamps <c>Ready</c> too when that is missing, because the server refuses a <c>Signed</c>
    /// without one (<c>contract_signed_requires_ready</c>) and a ONE-CLICK action must not be able to
    /// compose a write the server will reject. An existing ready date is kept, so signing never
    /// rewrites when the contract was sent out.
    /// </para>
    /// </summary>
    private Task MarkSigned(ContractListItem c) =>
        WriteSignature(
            c,
            (d, now) => (d.Ready ?? now, d.Signed ?? now),
            "Unable to mark the contract signed", "Contract marked signed.");

    /// <summary>
    /// Clears BOTH stamps, returning the contract to <c>Draft</c>. Never refused, in any state — a
    /// guard on the way out is how a row gets stranded, which is the rule the pause guard already
    /// states.
    /// </summary>
    private Task Unsign(ContractListItem c) =>
        WriteSignature(
            c,
            (_, _) => (null, null),
            "Unable to clear the signature dates", "Signature dates cleared.");

    /// <summary>
    /// The shared body of the three actions above: load the detail, rebuild the WHOLE record with the
    /// new pair of stamps, PUT it.
    ///
    /// <para>
    /// The rebuild carries every other field explicitly — <c>IsArchived</c>, <c>IsPaused</c> and the
    /// dates — for the reason the DTO documents: <c>PUT</c> is a full replacement, so an omitted
    /// field is a cleared field. Writing one stamp here and forgetting the archive flag would
    /// silently restore a contract the reader only meant to sign.
    /// </para>
    /// </summary>
    private async Task WriteSignature(
        ContractListItem c,
        Func<ExistingContract, DateTime, (DateTime? Ready, DateTime? Signed)> next,
        string failure, string success)
    {
        if (!_canUpdate) return;
        await EnsureDetail(c.ContractId);
        if (!_details.TryGetValue(c.ContractId, out var d))
            return;

        var (ready, signed) = next(d, DateTime.UtcNow);
        var update = new UpdateContract
        {
            Name = d.Name,
            Type = d.Type,
            Description = d.Description,
            StartDate = d.StartDate,
            EndDate = d.EndDate,
            CompletionDate = d.CompletionDate,
            IsArchived = d.Archived is not null,
            IsPaused = d.Paused is not null,
            Ready = ready,
            Signed = signed,
        };

        if ((await Contracts.UpdateAsync(c.ContractId, update)).Toast(Snackbar, failure, success))
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

    // ── Add/edit-party dialog ────────────────────────────────────────────────────
    // One dialog serves both (issue #121's new PUT): _editingParty is what switches it between
    // "New party" and "Edit party".
    private ExistingContract? _partyContract;
    private ExistingContractParty? _editingParty;
    private Guid _partyKey;
    private bool _partyOpen;

    private Task AddParty(Guid contractId) => OpenPartyDialog(contractId, party: null);

    /// <summary>
    /// Opens the Terms section's create dialog on an expanded record. The card is expanded first when
    /// it is not already: the dialog writes into a section the reader has to be able to see the result
    /// in, and a create that lands in a collapsed body reads as nothing having happened.
    /// </summary>
    /// <remarks>
    /// On a collapsed card the section does not exist yet — <c>ContractDetailView</c> renders only
    /// once the detail load has landed — so the open is DEFERRED to the render that brings it into
    /// existence rather than raced against it with a yield.
    /// </remarks>
    private async Task AddTerm(Guid contractId)
    {
        if (!IsExpanded(contractId))
        {
            await ToggleExpand(contractId);
        }

        // A fresh token each time, so clicking "New term" twice on the same record opens the dialog
        // twice rather than being swallowed as an unchanged parameter.
        _newTermRequest = (contractId, Guid.NewGuid());
        StateHasChanged();
    }

    /// <summary>
    /// The outstanding "New term" request: which record asked, and a token identifying the ask.
    /// </summary>
    /// <remarks>
    /// The request travels DOWN as a parameter rather than through an <c>@ref</c> to the expanded
    /// body. A ref is rebound on the next render, so immediately after expanding record B it still
    /// points at record A's section — and where B's detail was already cached, <c>ToggleExpand</c>
    /// returns without yielding at all, so no render has happened in between. Opening through the ref
    /// there would put the dialog on the wrong contract and silently drop the click. The token is
    /// handed only to the row whose id matches, so the section that receives it IS that contract's
    /// section, by construction rather than by timing.
    /// </remarks>
    private (Guid ContractId, Guid Token)? _newTermRequest;

    /// <summary>The token for this record, or null when the outstanding request is not its own.</summary>
    private Guid? NewTermRequestFor(Guid contractId) =>
        _newTermRequest is { } request && request.ContractId == contractId ? request.Token : null;

    /// <summary>
    /// Opens the Events section's create dialog on an expanded record. Same shape as
    /// <see cref="AddTerm"/>: the card is expanded first when it is not already, because the dialog
    /// writes into a section the reader has to be able to see the result in, and a create that lands
    /// in a collapsed body reads as nothing having happened.
    /// </summary>
    private async Task AddEvent(Guid contractId)
    {
        if (!IsExpanded(contractId))
        {
            await ToggleExpand(contractId);
        }

        // A fresh token each time, so clicking "New event" twice on the same record opens the dialog
        // twice rather than being swallowed as an unchanged parameter.
        _newEventRequest = (contractId, Guid.NewGuid());
        StateHasChanged();
    }

    /// <summary>
    /// The outstanding "New event" request: which record asked, and a token identifying the ask. The
    /// reasoning is <see cref="_newTermRequest"/>'s, unchanged — a ref to the expanded body is rebound
    /// on the next render, so it can still point at the previous record's section at the moment the
    /// click is handled.
    /// </summary>
    private (Guid ContractId, Guid Token)? _newEventRequest;

    /// <summary>The token for this record, or null when the outstanding request is not its own.</summary>
    private Guid? NewEventRequestFor(Guid contractId) =>
        _newEventRequest is { } request && request.ContractId == contractId ? request.Token : null;

    private Task EditParty(Guid contractId, ExistingContractParty party) => OpenPartyDialog(contractId, party);

    private async Task OpenPartyDialog(Guid contractId, ExistingContractParty? party)
    {
        if (!_canUpdate) return;
        await EnsureDetail(contractId);
        if (!_details.TryGetValue(contractId, out var d)) return;
        _expandedId = contractId;
        _partyContract = d;
        _editingParty = party;
        _partyKey = Guid.NewGuid();
        _partyOpen = true;
    }

    /// <summary>
    /// Routes a child's line into the page's own <c>OdsLiveAnnouncer</c>. The announcer is mounted
    /// once, on this page, so the party tiles and the dialog raise their text rather than each owning
    /// a live region — two regions on one page race each other.
    /// </summary>
    private void Announce(string message)
    {
        _announce = message;
        StateHasChanged();
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
    /// window or one suspended reads pending; everything else keeps the neutral ink, archived
    /// included — a retired record is not a problem.</summary>
    private static OdsRecordFigureTone HeadlineTone(string cls) => cls switch
    {
        "expired" => OdsRecordFigureTone.Expense,
        "soon" or "paused" => OdsRecordFigureTone.Pending,
        _ => OdsRecordFigureTone.Neutral,
    };

    /// <summary>The Status tile's value tint, from the same registry the status chip reads, so the
    /// chip in the header and the tile in the body can never disagree.</summary>
    private static OdsInfoTileTone StatusTone(string chipTone) => chipTone switch
    {
        "income" => OdsInfoTileTone.Income,
        "info" => OdsInfoTileTone.Info,
        "expense" => OdsInfoTileTone.Expense,
        "pending" => OdsInfoTileTone.Pending,
        _ => OdsInfoTileTone.Muted,
    };

    /// <summary>The date the current state began — a status is a reading of the record at a moment,
    /// so it carries when that moment was. Null where the record has no date to point at.</summary>
    private static string? StatusFoot(ExistingContract c, bool oneOff) => c.Status switch
    {
        ContractStatus.Archived => c.Archived is { } a ? $"since {LongDate(a)}" : null,
        ContractStatus.Draft => "not yet marked ready for signature",
        ContractStatus.Ready => c.Ready is { } r
            ? $"ready since {LongDate(r)} — waiting on a signature"
            : "waiting on a signature",
        ContractStatus.Paused => c.Paused is { } p ? $"since {LongDate(p)}" : null,
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
    /// but reads Active, so completion counts too. The test itself is
    /// <see cref="ContractLifecycle.HasEnded"/>, shared with the service.
    /// </para>
    /// </summary>
    private IReadOnlyList<OdsMenuItem> RowActions(ContractListItem c, bool archived)
    {
        // The SAME predicate the service enforces, not a second copy of its boundaries — it lives in
        // Odyssey.Dtos precisely so the disabled action and the 400 can never disagree.
        var hasEnded = ContractLifecycle.HasEnded(c.EndDate, c.CompletionDate, Today);
        var items = new List<OdsMenuItem>();

        // On file but not in force — the single predicate the money roll-ups gate on, used here so
        // the menu and the server can never disagree about which contracts are unsigned.
        var unsigned = ContractStatusOrder.IsUnsigned(c.Status);

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "edit",
                Label = "Edit contract",
                OnClick = EventCallback.Factory.Create(this, () => EditClicked(c)),
            });

            // The signature path (issue #145), FIRST while it is the thing the contract is waiting
            // on. Offered in lifecycle order, one step at a time: a contract already signed is never
            // offered "Mark ready". The dates are also editable in the dialog, for backdating.
            if (c.Signed is null && c.Ready is null)
            {
                items.Add(new OdsMenuItem
                {
                    Icon = "draw",
                    Label = "Mark ready for signature",
                    OnClick = EventCallback.Factory.Create(this, () => MarkReady(c)),
                });
            }
            else if (c.Signed is null)
            {
                items.Add(new OdsMenuItem
                {
                    Icon = "history_edu",
                    Label = "Mark signed",
                    OnClick = EventCallback.Factory.Create(this, () => MarkSigned(c)),
                });
            }

            // Offered whenever EITHER stamp exists, in any state — clearing is never refused, which
            // is what stops an archived or expired contract being stranded holding one.
            if (c.Signed is not null || c.Ready is not null)
            {
                items.Add(new OdsMenuItem
                {
                    Icon = "undo",
                    Label = c.Signed is not null ? "Unsign" : "Clear ready date",
                    OnClick = EventCallback.Factory.Create(this, () => Unsign(c)),
                });
            }

            // Pause is enterable from Active ALONE, so the action is simply ABSENT elsewhere rather
            // than disabled-with-a-reason like Archive. The difference is whether there is an
            // instruction to give: Archive's precondition ("the contract has to end first") is a step
            // the reader can act on, while "this contract is upcoming" is not. Resume is offered
            // wherever a stamp exists, in any state — clearing a pause is never refused, which is
            // what stops an archived or expired contract being stranded holding one.
            //
            // An UNSIGNED contract is the one non-Active case that DOES have an instruction to give —
            // sign it — so it gets the disabled-with-a-reason treatment instead of the silent
            // absence, which also keeps the item focusable for a keyboard or AT user (WCAG 2.1.1).
            if (c.Status == ContractStatus.Active)
            {
                items.Add(new OdsMenuItem
                {
                    Icon = "pause_circle",
                    Label = "Pause",
                    OnClick = EventCallback.Factory.Create(this, () => TogglePause(c)),
                });
            }
            else if (unsigned && c.Paused is null)
            {
                items.Add(new OdsMenuItem
                {
                    Icon = "pause_circle",
                    Label = "Pause",
                    Disabled = true,
                    Description = "Only a signed contract in force can be paused.",
                });
            }
            else if (c.Paused is not null)
            {
                items.Add(new OdsMenuItem
                {
                    Icon = "play_circle",
                    Label = "Resume",
                    OnClick = EventCallback.Factory.Create(this, () => TogglePause(c)),
                });
            }
            // The server refuses a party add on an archived contract (422), so the action is offered
            // with its reason rather than hidden — and Disabled + Description keeps it FOCUSABLE
            // (aria-disabled, no native disabled), so a keyboard or AT user can actually reach the
            // explanation instead of skipping a silent item (WCAG 2.1.1).
            items.Add(archived
                ? new OdsMenuItem
                {
                    Icon = "group_add",
                    Label = "New party",
                    Disabled = true,
                    Description = "Unarchive the contract to change its parties.",
                }
                : new OdsMenuItem
                {
                    Icon = "group_add",
                    Label = "New party",
                    OnClick = EventCallback.Factory.Create(this, () => AddParty(c.ContractId)),
                });

            // The server refuses every term write on an archived contract, so the action is offered
            // with its reason rather than hidden — the same Disabled + Description treatment New
            // party gets, which keeps the item FOCUSABLE so a keyboard or AT user reaches the
            // explanation instead of skipping a silent item (WCAG 2.1.1).
            items.Add(archived
                ? new OdsMenuItem
                {
                    Icon = "sell",
                    Label = "New term",
                    Disabled = true,
                    Description = "The contract has to be restored first.",
                }
                : new OdsMenuItem
                {
                    Icon = "sell",
                    Label = "New term",
                    OnClick = EventCallback.Factory.Create(this, () => AddTerm(c.ContractId)),
                });

            // NOT gated on the archive state, unlike the two above (issue #138 §8.6). The server
            // accepts an event write on an archived contract, so a disabled item here would refuse
            // something the API allows — archival hides a contract, it does not lock its history.
            items.Add(new OdsMenuItem
            {
                Icon = "history",
                Label = "New event",
                OnClick = EventCallback.Factory.Create(this, () => AddEvent(c.ContractId)),
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
            // An ENDED or an UNSIGNED contract can be archived (issue #145 widened the server rule):
            // abandoning a negotiation is the likeliest reason to archive a draft, and a draft
            // typically has no end date at all, so the un-widened rule would offer a step the reader
            // could never take.
            items.Add(hasEnded || archived || unsigned
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

    private static string LongDate(DateTime date) => date.ToString("MMM dd, yyyy", CultureInfo.CurrentCulture);

    // ── Collapsed headline figure (mirrors the design's conHeadline) ──────────────
    private (bool HasValue, string Value, string Word, string Cls) Headline(ContractListItem c)
    {
        // Paused: a countdown is meaningless while nothing is running, so the headline says when the
        // pause began instead. Checked HERE, above the one-off branch, for exactly the reason the
        // server applies Paused to the RESULT of its derivation — the one-off branch returns early,
        // so a paused settled one-off would otherwise keep counting down to a completion it reached.
        if (c.Status == ContractStatus.Paused)
        {
            return c.Paused is { } pausedAt
                ? (true, pausedAt.ToString("MMM dd, yyyy"), "paused", "paused")
                : (false, "Paused", "paused", "paused");
        }

        // Unsigned (issue #145): the term's dates describe something nobody has agreed to, so
        // counting down to them would assert a commitment that does not exist. The headline says
        // where the signature got to instead. Checked HERE, above the one-off branch, for exactly the
        // reason the server puts the layer above its date chain — the one-off branch returns early,
        // so a check placed after it would never run for an unsigned one-off.
        if (c.Status == ContractStatus.Draft)
        {
            // No date to point at, by definition — a draft is the state of having neither stamp — so
            // the figure is the word itself, the same shape the open-ended and archived fallbacks
            // already take rather than inventing an anchor.
            return (false, "Draft", "not yet ready for signature", "");
        }

        if (c.Status == ContractStatus.Ready)
        {
            return c.Ready is { } readyAt
                ? (true, readyAt.ToString("MMM dd, yyyy"), "ready for signature", "soon")
                : (false, "Ready", "ready for signature", "soon");
        }

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
