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

public partial class InsuranceCard
{
    // ── Data ────────────────────────────────────────────────────────────────
    private List<InsurancePolicyListItem> _policies = [];
    private readonly Dictionary<Guid, ExistingInsurancePolicy> _details = new();
    private InsurancePortfolioSummary? _summary;

    private Dictionary<string, string> _currencySymbols = new(StringComparer.OrdinalIgnoreCase);
    // One contact list, shared by the three contact pickers — insurers, insured contacts and
    // beneficiaries all choose from the whole address book (no contact-type restriction: a trust is a
    // legitimate beneficiary, a company a legitimate insured).
    private IReadOnlyList<OdsOption> _contactOptions = [];
    private IReadOnlyList<OdsOption> _accountOptions = [];
    private Dictionary<Guid, AccountType> _accountTypes = new();
    // True until both option lists have arrived. The dialog's pickers show their loading row rather
    // than "No contacts match", which would be indistinguishable from an empty address book.
    private bool _optionsLoading = true;

    private string? _baseCurrency;
    private Guid? _flashId;

    // Card-list windowing (OdsInfiniteList): "Load N at a time" batch size.
    private int _batch = OdsPageSizes.Batch[0];

    // ── UI state ─────────────────────────────────────────────────────────────
    private bool _isLoading = true;
    private bool _refetching;
    private bool _loadError;
    private string _announce = "";
    private Guid? _expandedId;

    // The list is server-filtered, so an empty result only means "first run" when nothing is filtering it.
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_searchString)
        || _typeFilter.Count > 0 || _statusFilter.Count > 0;

    // ── Persisted page state ───────────────────────────────────────────────────
    private const string PageStateKey = "insurance-policies-page";
    private bool _problemsOpen = true;
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _searchString = string.Empty;
    private IReadOnlyCollection<string> _typeFilter = [];
    private IReadOnlyCollection<string> _statusFilter = [];

    private static readonly IReadOnlyList<OdsOption> _statusOptions =
        [.. OdsCoverageStatus.Order.Select(s => new OdsOption(s.ToString(), OdsCoverageStatus.Meta(s).Label))];

    // ── Sort (§6.9) — toolbar OdsSortSelect is the sole sort surface (no headers).
    // Premium compares raw amounts across policies (no FX, §14). ──
    private static readonly OdsTableSort DefaultSort = new("name", OdsSortDirection.Asc);
    private OdsTableSort _sort = DefaultSort;
    private static readonly IReadOnlyList<OdsSortField<InsurancePolicyListItem>> _sortFields =
    [
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text, SortValue = p => p.Name.ToLowerInvariant() },
        new() { Key = "type", Label = "Type", Type = OdsSortType.Status, SortValue = p => (int)p.Type },
        new() { Key = "renewalEnd", Label = "Renewal end date", Type = OdsSortType.Date, SortValue = p => p.CurrentRenewalEndDate },
        new() { Key = "premium", Label = "Premium", Type = OdsSortType.Number, SortValue = p => p.CurrentPremium },
    ];

    // ── Permissions ────────────────────────────────────────────────────────────
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;
    private bool _canDownloadFiles;
    private bool _canUploadFiles;

    // ── Computed ────────────────────────────────────────────────────────────────
    // Derived from the unfiltered summary (issue #277): the sub-line reflects the whole set, not the
    // server-filtered display list. TotalPolicies is the live (non-archived) count already, so it IS
    // the active total — don't subtract archived again (unlike ContractSummary.TotalContracts, which
    // includes archived).
    private int ActiveCount => _summary?.TotalPolicies ?? 0;

    private static DateTime Today => DateTime.UtcNow.Date;

    private Func<decimal?, string?, string> MoneyFunc => Money;
    private Func<decimal?, string?, string> MoneyCompactFunc => MoneyCompact;
    private Func<AccountType, string> AccountTypeLabel => AccountTypeVisuals.Label;


    // ── Lifecycle ────────────────────────────────────────────────────────────────
    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await RestorePageStateAsync();
        await LoadPermissionsAsync();
        _baseCurrency = UserPreferences.DefaultCurrency;
        await Task.WhenAll(LoadPolicies(), LoadSummary(), LoadCurrencies(), LoadReferenceOptions());
    }

    // ── Page-state persistence ─────────────────────────────────────────────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<InsurancePageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(InsurancePageState state)
    {
        _problemsOpen = state.ProblemsOpen;
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _searchString = state.Search ?? string.Empty;
        _typeFilter = OdsTypeRegistries.InsurancePolicyOptions.KnownValues(state.TypeFilter);
        _statusFilter = _statusOptions.KnownValues(state.StatusFilter);
        _sort = OdsSortHelpers.Resolve(_sortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private InsurancePageState BuildPageState() => new()
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
    private async Task OnTypeFilterChanged(IReadOnlyCollection<string> values) { _typeFilter = values ?? []; PersistPageState(); await LoadPolicies(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await LoadPolicies(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await LoadPolicies(); }
    private void OnBatchChanged(int size) { _batch = size; PersistPageState(); StateHasChanged(); }

    private async Task ClearFilters()
    {
        _searchString = string.Empty;
        _typeFilter = [];
        _statusFilter = [];
        PersistPageState();
        await LoadPolicies();
    }

    private sealed class InsurancePageState
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
        _canCreate = user.HasPermission(PermissionClaims.InsuranceCreate);
        _canUpdate = user.HasPermission(PermissionClaims.InsuranceUpdate);
        _canDelete = user.HasPermission(PermissionClaims.InsuranceDelete);
        _canDownloadFiles = user.HasPermission(PermissionClaims.FilesRead);
        _canUploadFiles = user.HasPermission(PermissionClaims.FilesCreate)
                       && user.HasPermission(PermissionClaims.FilesRead)
                       && user.HasPermission(PermissionClaims.InsuranceUpdate);
    }

    private async Task LoadPolicies()
    {
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        // Track failure explicitly: ItemsOrToast falls back to [], which is indistinguishable from a
        // genuinely empty set and would render the onboarding empty state after a 500.
        var result = await Insurance.ListAsync(
            _searchString,
            _typeFilter,
            _statusFilter,
            _sort.Key,
            _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc");

        _policies = result.ItemsOrToast(Snackbar, "insurance policies");
        _loadError = !result.IsSuccess;

        _announce = _loadError ? "Couldn't load insurance policies."
            : _policies.Count == 0 ? "No policies match your filters."
            : $"Showing {_policies.Count} polic{(_policies.Count == 1 ? "y" : "ies")}.";
        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    private async Task LoadSummary()
    {
        _summary = await Insurance.GetSummaryAsync(_baseCurrency);
        StateHasChanged();
    }

    private async Task LoadCurrencies()
    {
        var currencies = await ReferenceData.CurrenciesAsync();
        _currencySymbols = currencies
            .GroupBy(c => c.CurrencyCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Symbol, StringComparer.OrdinalIgnoreCase);
    }

    private async Task LoadContacts()
    {
        var contacts = await ReferenceData.ContactsAsync();
        _contactOptions =
        [
            .. contacts
                .Where(c => c.Archived is null)
                .OrderBy(c => c.ResolvedDisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(c =>
                {
                    var meta = OdsTypeRegistries.ContactTypeOf(c.Type.ToString());
                    // Sub carries the type in TEXT beside the name: three of the four pickers choose
                    // from the whole address book, where two records can share a display name and the
                    // glyph alone cannot tell a person from an organisation.
                    return new OdsOption(c.ContactId.ToString(), c.ResolvedDisplayName)
                    {
                        Icon = meta.Icon,
                        IconColor = meta.Color,
                        Sub = meta.Label,
                    };
                })
        ];
    }

    /// <summary>
    /// Re-reads both option lists after a save was rejected for naming a record that no longer
    /// resolves. The cache holds until it is invalidated, so a plain reload would re-serve the same
    /// stale list the save just failed on.
    /// </summary>
    private async Task ReloadReferenceOptions()
    {
        ReferenceData.InvalidateContacts();
        _optionsLoading = true;
        StateHasChanged();
        await LoadReferenceOptions();
    }

    private async Task LoadReferenceOptions()
    {
        try
        {
            await Task.WhenAll(LoadContacts(), LoadAccounts());
        }
        finally
        {
            _optionsLoading = false;
            StateHasChanged();
        }
    }

    private async Task LoadAccounts()
    {
        var accounts = (await Accounts.ListAllAsync()).ItemsOrToast(Snackbar, "accounts");
        var active = accounts.Where(a => a.Archived is null).ToList();
        _accountTypes = active.ToDictionary(a => a.AccountId, a => a.AccountType);
        _accountOptions =
        [
            .. active
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(a => new OdsOption(a.AccountId.ToString(), a.Name)
                {
                    Icon = AccountTypeVisuals.MaterialIcon(a.AccountType),
                    IconColor = AccountTypeVisuals.FgColor(a.AccountType),
                    Sub = AccountTypeVisuals.Label(a.AccountType),
                })
        ];
    }

    // ── Link collections: meta line and detail tiles (issue #27 §3) ────────────

    /// <summary>
    /// The meta line's insurer segment: up to TWO named, then "+N". An unnamed member reads as its
    /// STATE, never as a name and never as a GUID — the read model carries no name for it.
    /// </summary>
    internal static string InsurerNames(IReadOnlyList<PolicyContactReference> insurers)
    {
        const int named = 2;
        var shown = insurers.Take(named).Select(DisplayOf);
        var rest = insurers.Count - Math.Min(named, insurers.Count);
        return string.Join(", ", shown) + (rest > 0 ? $" +{rest}" : string.Empty);
    }

    /// <summary>
    /// The segment's glyph: the contact type's own where every insurer shares one, else the generic
    /// "link" glyph. Never an arbitrary type glyph over a mixed collection — a single icon cannot
    /// stand for two types, and the names beside it are what carry the meaning anyway.
    /// </summary>
    internal static string InsurerGlyph(IReadOnlyList<PolicyContactReference> insurers)
    {
        var types = insurers.Select(i => i.Type).Where(t => t is not null).Distinct().ToList();
        return types.Count == 1
            ? OdsTypeRegistries.ContactTypeOf(types[0]!.Value.ToString()).Icon
            : "link";
    }

    internal static string DisplayOf(PolicyContactReference reference) => reference.Availability switch
    {
        LinkAvailability.Archived => "Archived",
        LinkAvailability.Unresolvable => "Unavailable",
        _ => reference.Name ?? "Unavailable",
    };

    internal static IReadOnlyList<InsurancePolicyLinkTiles.LinkTileMember> ContactMembers(
        IReadOnlyList<PolicyContactReference> references) =>
    [
        .. references.Select(reference =>
        {
            // A resolved type still carries its glyph and its label in TEXT; an unresolvable link has
            // no contact row to read either from, so it states neither.
            var meta = reference.Type is { } type ? OdsTypeRegistries.ContactTypeOf(type.ToString()) : null;
            return new InsurancePolicyLinkTiles.LinkTileMember
            {
                Key = reference.ContactId.ToString(),
                Display = DisplayOf(reference),
                TypeLabel = meta?.Label,
                Icon = meta?.Icon,
                IconColor = meta?.Color,
                IconSoft = meta?.Soft,
                State = reference.Availability,
            };
        })
    ];

    internal static IReadOnlyList<InsurancePolicyLinkTiles.LinkTileMember> AccountMembers(
        IReadOnlyList<InsuredAccountReference> references) =>
    [
        .. references.Select(reference => new InsurancePolicyLinkTiles.LinkTileMember
        {
            Key = reference.AccountId.ToString(),
            Display = reference.Name,
            TypeLabel = AccountTypeVisuals.Label(reference.Type),
            Icon = AccountTypeVisuals.MaterialIcon(reference.Type),
            IconColor = AccountTypeVisuals.FgColor(reference.Type),
            IconSoft = AccountTypeVisuals.BgColor(reference.Type),
        })
    ];

    // ── Header problem rollup (ExpiringSoon / Lapsed) ──────────────────────────
    private List<PageHeaderProblem> HeaderProblems =>
        _policies
            .Where(p => p.Archived is null
                && p.CoverageStatus is CoverageStatus.ExpiringSoon or CoverageStatus.Lapsed)
            .Select(p => new PageHeaderProblem
            {
                Severity = p.CoverageStatus == CoverageStatus.Lapsed ? PageHeaderSeverity.Error : PageHeaderSeverity.Warning,
                Lead = p.Name,
                Message = p.CoverageStatus == CoverageStatus.Lapsed
                    ? "Coverage has lapsed — review its renewal periods."
                    : $"Coverage expiring soon{(p.CurrentRenewalEndDate is { } end ? $" — ends {end:MMM dd, yyyy}" : "")}.",
                OnView = EventCallback.Factory.Create(this, () => JumpTo(p.InsurancePolicyId)),
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
            await ScrollManager.ScrollIntoViewAsync($"#ins-{id}", ScrollBehavior.Smooth);
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

        var policy = await Insurance.GetAsync(id);
        if (policy is not null)
            _details[id] = policy;
        StateHasChanged();
    }

    private async Task ReloadPolicy(Guid id)
    {
        var policy = await Insurance.GetAsync(id);
        if (policy is not null)
            _details[id] = policy;
        await LoadPolicies();
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

    private async Task OnPolicyCreated()
    {
        await LoadPolicies();
        await LoadSummary();
    }

    // ── Edit mode (design-system update: the create dialog reused in edit mode, not an inline panel) ──
    private ExistingInsurancePolicy? _editPolicy;
    private Guid _editPolicyKey;
    private bool _editPolicyOpen;

    private async Task EditClicked(InsurancePolicyListItem p)
    {
        if (!_canUpdate) return;

        // The dialog prefills from the full record, so make sure the detail is cached first.
        await EnsureDetail(p.InsurancePolicyId);
        if (!_details.TryGetValue(p.InsurancePolicyId, out var detail))
            return;

        _editPolicy = detail;
        _editPolicyKey = Guid.NewGuid();
        _editPolicyOpen = true;
    }

    private async Task OnPolicyEdited()
    {
        if (_editPolicy is null) return;
        await ReloadPolicy(_editPolicy.InsurancePolicyId);
    }

    // ── Archive / unarchive (PUT with the Archived flag; archived policies stay in the list,
    //    dimmed, and drop out of the portfolio summary) ───────────────────────────────────
    private async Task ToggleArchive(InsurancePolicyListItem p)
    {
        if (!_canUpdate) return;
        await EnsureDetail(p.InsurancePolicyId);
        if (!_details.TryGetValue(p.InsurancePolicyId, out var d))
            return;

        var archiving = p.Archived is null;
        var update = new UpdateInsurancePolicy
        {
            Name = d.Name,
            PolicyNumber = d.PolicyNumber,
            Type = d.Type,
            // Archive/unarchive touches the policy's own state only, so every link collection is left
            // NULL — the "leave unchanged" shorthand. Echoing the four sets back would be a needless
            // full-set diff, and would fail outright on a policy holding an archived link.
            Notes = d.Notes,
            Archived = archiving,
        };

        if ((await Insurance.UpdateAsync(p.InsurancePolicyId, update)).Toast(Snackbar,
                archiving ? "Unable to archive policy" : "Unable to unarchive policy",
                archiving ? "Policy archived." : "Policy unarchived."))
        {
            await ReloadPolicy(p.InsurancePolicyId);
        }
    }

    // ── Delete (permanent; the API hard-deletes the policy + its renewals/file links) ──────
    private async Task ConfirmDelete(InsurancePolicyListItem p)
    {
        if (!_canDelete) return;
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete insurance policy",
            $"Permanently delete '{p.Name}' and all its renewal periods and document links? This cannot be undone.",
            yesText: "Delete", cancelText: "Cancel");

        if (confirmed == true && (await Insurance.DeleteAsync(p.InsurancePolicyId)).Toast(Snackbar, "Delete failed", "Policy deleted."))
        {
            _policies.Remove(p);
            _details.Remove(p.InsurancePolicyId);
            if (_expandedId == p.InsurancePolicyId) _expandedId = null;
            if (_editPolicy?.InsurancePolicyId == p.InsurancePolicyId) _editPolicy = null;
            await LoadSummary();
            StateHasChanged();
        }
    }

    private Task CopyId(Guid id) => Clipboard.CopyAsync(id.ToString(), "Policy ID copied.");

    // ── Renewal dialog ─────────────────────────────────────────────────────────
    private ExistingInsurancePolicy? _renewalPolicy;
    private ExistingPolicyRenewal? _editingRenewal;
    private Guid _renewalKey;
    private bool _renewalOpen;

    private async Task AddRenewal(Guid policyId)
    {
        if (!_canUpdate) return;
        await EnsureDetail(policyId);
        if (!_details.TryGetValue(policyId, out var d)) return;
        _expandedId = policyId;
        _renewalPolicy = d;
        _editingRenewal = null;
        _renewalKey = Guid.NewGuid();
        _renewalOpen = true;
    }

    private void EditRenewal(Guid policyId, ExistingPolicyRenewal renewal)
    {
        if (!_canUpdate || !_details.TryGetValue(policyId, out var d)) return;
        _renewalPolicy = d;
        _editingRenewal = renewal;
        _renewalKey = Guid.NewGuid();
        _renewalOpen = true;
    }

    private async Task DeleteRenewal(Guid policyId, ExistingPolicyRenewal renewal)
    {
        if (!_canUpdate) return;
        var confirmed = await DialogService.ShowMessageBoxAsync(
            "Delete renewal period",
            $"Remove the period {renewal.FromDate:MMM dd, yyyy} → {renewal.ToDate:MMM dd, yyyy}? This can't be undone.",
            yesText: "Delete", cancelText: "Cancel");

        if (confirmed == true
            && (await Insurance.DeleteRenewalAsync(policyId, renewal.PolicyRenewalId))
                .Toast(Snackbar, "Unable to delete renewal", "Renewal period deleted."))
        {
            await ReloadPolicy(policyId);
        }
    }

    // ── Upload / attach dialog ───────────────────────────────────────────────────
    private ExistingInsurancePolicy? _uploadPolicy;
    private Guid _uploadRenewalId;
    private bool _uploadLockPeriod;
    private Guid _uploadKey;
    private bool _uploadOpen;

    /// <summary>
    /// Opens the attach dialog against ONE period — the only place an insurance document can live
    /// (issue #26).
    ///
    /// <para>
    /// The period panel passes its own id, and that target is the user's own choice, so the dialog
    /// locks it. The row menu has no such context: the target is inferred by
    /// <see cref="InsuranceAttachTarget"/> and the dialog offers it as a defaulted picker instead, so
    /// a late-arriving document can still be filed against an earlier period.
    /// </para>
    /// </summary>
    private async Task AttachDocument(Guid policyId, Guid? renewalId = null)
    {
        if (!_canUploadFiles) return;
        await EnsureDetail(policyId);
        if (!_details.TryGetValue(policyId, out var d)) return;

        var target = InsuranceAttachTarget.Resolve(d, renewalId);
        if (target == Guid.Empty)
        {
            // The gate on RenewalCount should have disabled the action; this is the race where the
            // last period was deleted underneath an open record.
            Snackbar.Add("Add a renewal period before attaching a document.", Severity.Warning);
            return;
        }

        _expandedId = policyId;
        _uploadPolicy = d;
        _uploadRenewalId = target;
        _uploadLockPeriod = renewalId is not null;
        _uploadKey = Guid.NewGuid();
        _uploadOpen = true;
    }

    /// <summary>
    /// After an attach: reload, then ANNOUNCE the period's new document count.
    ///
    /// <para>
    /// The count is written to the documents chip's <c>aria-label</c>, and a changed label on an
    /// UNFOCUSED control is not announced — focus is back on the Attach button, not the chip. So the
    /// live region carries it (WCAG 2.2 §4.1.3), the same way BudgetsCard announces its edit-mode
    /// transition.
    /// </para>
    /// </summary>
    private async Task OnDocumentsAttached(Guid policyId, Guid renewalId)
    {
        await ReloadPolicy(policyId);

        // The period the documents actually landed on — which the picker may have changed — so the
        // announcement names it rather than "this period".
        if (_details.TryGetValue(policyId, out var d)
            && d.Renewals.FirstOrDefault(r => r.PolicyRenewalId == renewalId) is { } period)
        {
            _announce = InsuranceAnnouncements.DocumentsOnPeriod(period);
            StateHasChanged();
        }
    }

    /// <summary>
    /// After a renewal period is saved: reload, and when the policy had none before, announce that
    /// attaching documents is now possible — the row menu's Attach document has just gone from
    /// disabled to enabled, which is a state change nothing else would voice.
    /// </summary>
    private async Task OnRenewalSaved(Guid policyId)
    {
        var before = PeriodCount(policyId);
        await ReloadPolicy(policyId);

        if (InsuranceAnnouncements.PeriodsBecameAvailable(before, PeriodCount(policyId)) is { } message)
        {
            _announce = message;
            StateHasChanged();
        }
    }

    /// <summary>The LIST row's period count — the same source the row-menu gate reads, so the
    /// announcement and the gate can never disagree about whether a policy has a period.</summary>
    private int PeriodCount(Guid policyId) =>
        _policies.FirstOrDefault(p => p.InsurancePolicyId == policyId)?.RenewalCount ?? 0;

    // ── Collapsed headline figure ──────────────────────────────────────────────
    // ── Record-card presentation ──────────────────────────────────────────────────

    /// <summary>The headline figure's colour role: lapsed cover reads expense, cover inside the
    /// expiring window reads pending, everything else keeps the neutral ink.</summary>
    private static OdsRecordFigureTone HeadlineTone(string cls) => cls switch
    {
        "lapsed" => OdsRecordFigureTone.Expense,
        "soon" => OdsRecordFigureTone.Pending,
        _ => OdsRecordFigureTone.Neutral,
    };

    /// <summary>The Status tile's value tint, from the same registry the coverage chip reads, so the
    /// chip in the header and the tile in the body can never disagree.</summary>
    private static OdsInfoTileTone StatusTone(string chipTone) => chipTone switch
    {
        "income" => OdsInfoTileTone.Income,
        "pending" => OdsInfoTileTone.Pending,
        "expense" => OdsInfoTileTone.Expense,
        "info" => OdsInfoTileTone.Info,
        _ => OdsInfoTileTone.Muted,
    };

    /// <summary>
    /// The Status tile's foot: the date the current state began. It names the period the STATUS
    /// refers to, which is only the <em>current</em> one while cover is in force — a lapsed policy
    /// points at its most recent period, an upcoming one at its earliest future period.
    /// </summary>
    private static string StatusFoot(ExistingInsurancePolicy p)
    {
        if (p.CoverageStatus == CoverageStatus.Archived)
        {
            return p.Archived is { } a ? $"since {LongDate(a)}" : "archived";
        }

        if (p.CoverageStatus is CoverageStatus.Active or CoverageStatus.ExpiringSoon && p.CurrentRenewal is { } current)
        {
            return $"this period ends {LongDate(current.ToDate)}";
        }

        if (p.CoverageStatus == CoverageStatus.Upcoming)
        {
            var next = p.Renewals.Where(r => r.FromDate.Date > Today).OrderBy(r => r.FromDate).FirstOrDefault();
            return next is null ? "no renewal period on record" : $"starts {LongDate(next.FromDate)}";
        }

        if (p.CoverageStatus == CoverageStatus.Lapsed)
        {
            var last = p.Renewals.Where(r => r.ToDate.Date < Today).OrderByDescending(r => r.ToDate).FirstOrDefault();
            return last is null ? "no renewal period on record" : $"ended {LongDate(last.ToDate)}";
        }

        return "no renewal period on record";
    }

    /// <summary>The row action menu, permission-gated. Archive stays ungated here: an insurance
    /// policy's lifecycle is not ordered the way a subscription's or a contract's is — cover can be
    /// retired at any point, and the design system's policy menu offers it unconditionally.</summary>
    private IReadOnlyList<OdsMenuItem> RowActions(InsurancePolicyListItem p, bool archived)
    {
        var items = new List<OdsMenuItem>();

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "edit",
                Label = "Edit policy",
                OnClick = EventCallback.Factory.Create(this, () => EditClicked(p)),
            });
            items.Add(new OdsMenuItem
            {
                Icon = "event_repeat",
                Label = "New renewal period",
                OnClick = EventCallback.Factory.Create(this, () => AddRenewal(p.InsurancePolicyId)),
            });
        }

        if (_canUploadFiles)
        {
            // Gated on the LIST item's count, not the detail's: RowActions renders from the list item
            // and _details is populated only for EXPANDED rows, so reading Renewals.Count there would
            // disable the action on every collapsed row. ReloadPolicy refreshes the list too, so the
            // enable transition after the first period is saved comes free.
            var hasPeriod = p.RenewalCount > 0;
            items.Add(new OdsMenuItem
            {
                Icon = "upload_file",
                Label = "Attach document",
                Disabled = !hasPeriod,
                // A document belongs to a period, so with no period there is nothing to attach to.
                // The reason is TEXT, associated as a description — never the dimmed styling alone.
                Description = hasPeriod ? null : "Add a renewal period first.",
                OnClick = EventCallback.Factory.Create(this, () => AttachDocument(p.InsurancePolicyId)),
            });
        }

        items.Add(new OdsMenuItem
        {
            Icon = "fingerprint",
            Label = "Copy ID",
            TrailingIcon = "content_copy",
            OnClick = EventCallback.Factory.Create(this, () => CopyId(p.InsurancePolicyId)),
        });

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem { Divider = true });
            items.Add(new OdsMenuItem
            {
                Icon = archived ? "unarchive" : "archive",
                Label = archived ? "Unarchive" : "Archive",
                OnClick = EventCallback.Factory.Create(this, () => ToggleArchive(p)),
            });
        }

        if (_canDelete)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "delete",
                Label = "Delete",
                Danger = true,
                OnClick = EventCallback.Factory.Create(this, () => ConfirmDelete(p)),
            });
        }

        return items;
    }

    private static string LongDate(DateTime date) => date.ToString("MMM dd, yyyy", CultureInfo.CurrentCulture);

    /// <summary>The collapsed row's headline figure — see <see cref="InsuranceHeadline"/>, which owns
    /// the branches so they can be tested without a rendered card.</summary>
    private static InsuranceHeadlineFigure Headline(InsurancePolicyListItem p) =>
        InsuranceHeadline.Compute(p, Today);

    // ── Money ────────────────────────────────────────────────────────────────────
    private string Money(decimal? n, string? code)
    {
        if (n is null) return "—";
        var sign = n.Value < 0 ? "−" : string.Empty;
        return $"{sign}{Symbol(code)} {Math.Abs(n.Value).ToString("#,##0.##", CultureInfo.InvariantCulture)}";
    }

    private string MoneyCompact(decimal? n, string? code)
    {
        if (n is null) return "—";
        var sym = Symbol(code);
        var abs = Math.Abs(n.Value);
        var sign = n.Value < 0 ? "−" : string.Empty;
        if (abs >= 1_000_000) return $"{sign}{sym} {(abs / 1_000_000m):0.#}M";
        if (abs >= 1_000) return $"{sign}{sym} {(abs / 1_000m):0.#}k";
        return $"{sign}{sym} {abs.ToString("#,##0", CultureInfo.InvariantCulture)}";
    }

    private string Symbol(string? code) =>
        code is not null && _currencySymbols.TryGetValue(code, out var s) && !string.IsNullOrWhiteSpace(s) ? s : code ?? string.Empty;
}
