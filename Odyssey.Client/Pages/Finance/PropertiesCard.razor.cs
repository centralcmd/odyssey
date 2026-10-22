using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Odyssey.ApiClient;
using Odyssey.Client.Authorization;
using Odyssey.Client.Components;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class PropertiesCard
{
    // ── Data ────────────────────────────────────────────────────────────────
    private List<ExistingProperty> _properties = [];
    private PropertySummary? _summary;
    private Dictionary<string, ExistingCurrency> _currenciesByCode = new(StringComparer.OrdinalIgnoreCase);

    private Guid? _flashId;
    private int _batch = OdsPageSizes.Batch[0];

    // ── UI state ─────────────────────────────────────────────────────────────
    private bool _isLoading = true;
    private bool _refetching;
    // Latest-request-wins for the list fetch (issue #249): a superseded response touches nothing.
    private readonly ListLoader _listLoader = new();
    private bool _loadError;
    private string _announce = "";
    private Guid? _expandedId;

    /// <summary>One token per property whose row menu asked for a New estimate; see
    /// <see cref="PropertyEstimatesSection.NewEstimateRequestToken"/>.</summary>
    private readonly Dictionary<Guid, Guid> _newEstimateTokens = new();

    /// <summary>One token per property whose row menu asked to attach documents; see
    /// <see cref="PropertyDocumentsSection.AttachRequestToken"/>.</summary>
    private readonly Dictionary<Guid, Guid> _attachTokens = new();

    /// <summary>
    /// Document counts the Documents section has reported. There is no count on
    /// <see cref="ExistingProperty"/> (issue #210 Non-Goal 4), so a property never opened has none and
    /// the delete dialog states the cascade without a number rather than as "no documents".
    /// </summary>
    private readonly Dictionary<Guid, int> _documentCounts = new();

    /// <summary>One token per property whose row menu asked for a New event; see
    /// <see cref="PropertyEventsSection.NewEventRequestToken"/>.</summary>
    private readonly Dictionary<Guid, Guid> _newEventTokens = new();

    // ── Persisted page state ───────────────────────────────────────────────────
    private const string PageStateKey = "properties-page";
    private bool _problemsOpen = true;
    private bool _overviewOpen = true;
    private bool _searchOpen = true;
    private string _searchString = string.Empty;
    private IReadOnlyCollection<string> _typeFilter = [];
    private IReadOnlyCollection<string> _statusFilter = [];

    // ── Sort — the toolbar OdsSortSelect is the only sort surface; the server applies it. ──
    private static readonly OdsTableSort DefaultSort = new("name", OdsSortDirection.Asc);
    private OdsTableSort _sort = DefaultSort;

    private static readonly OdsSortField<ExistingProperty>[] BaseSortFields =
    [
        new() { Key = "name", Label = "Name", Type = OdsSortType.Text, SortValue = p => p.Name.ToLowerInvariant() },
        new() { Key = "type", Label = "Type", Type = OdsSortType.Status, SortValue = p => (int)p.Type },
        new() { Key = "acquired", Label = "Acquired", Type = OdsSortType.Date, SortValue = p => p.AcquiredDate },
    ];

    /// <summary>
    /// PropertySortBy.Value — the in-force estimate as of now, in each property's own currency, nulls
    /// last both ways. Offered only with <c>properties.estimates.read</c>: the server refuses it
    /// without, because the order alone says how the properties rank by worth.
    /// </summary>
    private static readonly OdsSortField<ExistingProperty> ValueSortField =
        new() { Key = "value", Label = "Value", Type = OdsSortType.Number, SortValue = p => p.CurrentEstimatedValue };

    private IReadOnlyList<OdsSortField<ExistingProperty>> SortFields =>
        _canReadEstimates ? [.. BaseSortFields, ValueSortField] : BaseSortFields;

    // ── Permissions ────────────────────────────────────────────────────────────
    private bool _canCreate;
    private bool _canUpdate;
    private bool _canDelete;
    private bool _canReadEstimates;
    private bool _canWriteEstimates;
    private bool _canReadTransactions;
    private bool _canReadContracts;
    private bool _canReadFiles;

    /// <summary>POST …/files needs properties.update AND files.read (issue #210 §7.2).</summary>
    private bool CanAttachDocuments => _canUpdate && _canReadFiles;

    // ── Dialogs ──────────────────────────────────────────────────────────────────
    private Guid _createKey = Guid.Empty;
    private bool _createOpen;
    private ExistingProperty? _editProperty;
    private Guid _editKey;
    private bool _editOpen;
    private ExistingProperty? _deleteProperty;
    private Guid _deleteKey;
    private bool _deleteOpen;

    // ── Computed ────────────────────────────────────────────────────────────────
    private bool _hasFilters => !string.IsNullOrWhiteSpace(_searchString)
        || _typeFilter.Count > 0 || _statusFilter.Count > 0;

    private static DateTime Today => DateTime.UtcNow.Date;

    // From the unfiltered summary, so the sub-line describes the whole file, not the filtered list.
    private string SubLineText => _summary is { } s
        ? $"{s.ByStatus.Owned} owned · {s.TotalProperties} on file"
        : "";

    /// <summary>
    /// Sorting by value compares raw amounts, so across currencies the order means little — the page
    /// says so rather than letting a reader take it for a converted ranking.
    /// </summary>
    private bool MixedCurrencySort => _sort.Key == "value"
        && _properties.Select(p => p.CurrencyCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

    // ── Lifecycle ────────────────────────────────────────────────────────────────
    protected override async Task OnInitializedAsync()
    {
        if (!OperatingSystem.IsBrowser())
            return;

        await LoadPermissionsAsync();
        await RestorePageStateAsync();
        await Task.WhenAll(LoadProperties(), LoadSummary(), LoadCurrencies());
    }

    private async Task LoadPermissionsAsync()
    {
        var user = await AuthenticationStateProvider.GetUserAsync();
        _canCreate = user.HasPermission(PermissionClaims.PropertiesCreate);
        _canUpdate = user.HasPermission(PermissionClaims.PropertiesUpdate);
        _canDelete = user.HasPermission(PermissionClaims.PropertiesDelete);
        _canReadEstimates = user.HasPermission(PermissionClaims.PropertiesEstimatesRead);
        _canWriteEstimates = user.HasPermission(PermissionClaims.PropertiesEstimatesWrite);
        _canReadTransactions = user.HasPermission(PermissionClaims.TransactionsRead);
        _canReadContracts = user.HasPermission(PermissionClaims.ContractsRead);
        _canReadFiles = user.HasPermission(PermissionClaims.FilesRead);
    }

    // ── Page-state persistence ─────────────────────────────────────────────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<PropertiesPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(PropertiesPageState state)
    {
        _problemsOpen = state.ProblemsOpen;
        _overviewOpen = state.OverviewOpen;
        _searchOpen = state.SearchOpen;
        _searchString = state.Search ?? string.Empty;
        _typeFilter = OdsTypeRegistries.PropertyOptions.KnownValues(state.TypeFilter);
        _statusFilter = PropertyVisuals.StatusOptions.KnownValues(state.StatusFilter);
        // Resolved against the fields THIS reader may use, so a stored "value" sort from a session
        // that held the estimates claim falls back to the default instead of drawing a 403.
        _sort = OdsSortHelpers.Resolve(SortFields, state.SortField, state.SortDirection, DefaultSort);
        _batch = OdsPageSizes.Restore(state.BatchSize, OdsPageSizes.Batch);
    }

    private PropertiesPageState BuildPageState() => new()
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
    private async Task OnTypeFilterChanged(IReadOnlyCollection<string> values) { _typeFilter = values ?? []; PersistPageState(); await LoadProperties(); }
    private async Task OnStatusFilterChanged(IReadOnlyCollection<string> values) { _statusFilter = values ?? []; PersistPageState(); await LoadProperties(); }
    private async Task OnSortChanged(OdsTableSort sort) { _sort = sort; PersistPageState(); await LoadProperties(); }
    private void OnBatchChanged(int size) { _batch = size; PersistPageState(); StateHasChanged(); }

    private async Task ClearFilters()
    {
        _searchString = string.Empty;
        _typeFilter = [];
        _statusFilter = [];
        PersistPageState();
        await LoadProperties();
    }

    private sealed class PropertiesPageState
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

    // ── Loading ──────────────────────────────────────────────────────────────────
    private async Task LoadProperties()
    {
        if (!_isLoading)
        {
            _refetching = true;
            StateHasChanged();
        }

        // ItemsOrToast falls back to [], which is indistinguishable from a genuinely empty file and
        // would render the first-run empty state after a 500 — so the failure is tracked explicitly.
        var response = await _listLoader.RunAsync(ct => Properties.ListAllAsync(
            _searchString,
            _typeFilter,
            _statusFilter,
            _sort.Key,
            _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc",
            ct));
        if (response.IsSuperseded)
            return;

        var result = response.Value;

        _properties = result.ItemsOrToast(Snackbar, "properties");
        _loadError = !result.IsSuccess;

        _announce = _loadError ? "Couldn't load properties."
            : _properties.Count == 0 ? "No properties match your filters."
            : $"Showing {_properties.Count} propert{(_properties.Count == 1 ? "y" : "ies")}.";
        _isLoading = false;
        _refetching = false;
        StateHasChanged();
    }

    private async Task LoadSummary()
    {
        // The total converts into the reader's own display currency; blank lets the server pick.
        var result = await Properties.GetSummaryAsync(UserPreferences.DefaultCurrency);
        _summary = result.IsSuccess ? result.Value : _summary;
        StateHasChanged();
    }

    private async Task LoadCurrencies()
    {
        var currencies = await ReferenceData.CurrenciesAsync();
        _currenciesByCode = currencies.ToDictionary(c => c.CurrencyCode, c => c, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-reads one property after a write that changes its row — an edit, an archive, an estimate —
    /// and patches it in place, so the open card keeps its position and its sections their state.
    /// </summary>
    private async Task ReloadProperty(Guid id)
    {
        var result = await Properties.GetAsync(id);
        if (result.IsSuccess && result.Value is { } fresh)
        {
            var index = _properties.FindIndex(p => p.PropertyId == id);
            if (index >= 0)
                _properties[index] = fresh;
        }

        await LoadSummary();
        StateHasChanged();
    }

    private void OnDocumentCountChanged(Guid id, int count) => _documentCounts[id] = count;

    private int? DocumentCountFor(Guid id) => _documentCounts.TryGetValue(id, out var count) ? count : null;

    private void OnSmartTagCountChanged(ExistingProperty property, int count)
    {
        property.SmartTagCount = count;
        StateHasChanged();
    }

    // ── Expand ──────────────────────────────────────────────────────────────────
    private bool IsExpanded(Guid id) => _expandedId == id;

    /// <summary>
    /// Any change of the open card consumes the pending row-menu requests. A section is created afresh
    /// on every expand and cannot remember a token it already handled, so a token left in place would
    /// reopen its dialog each time the card is reopened.
    /// </summary>
    private void ToggleExpand(Guid id)
    {
        _expandedId = _expandedId == id ? null : id;
        _attachTokens.Clear();
        _newEstimateTokens.Clear();
        _newEventTokens.Clear();
    }

    // ── Row menu ──────────────────────────────────────────────────────────────────
    private IReadOnlyList<OdsMenuItem> RowActions(ExistingProperty p)
    {
        var items = new List<OdsMenuItem>();

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "edit",
                Label = "Edit property",
                OnClick = EventCallback.Factory.Create(this, () => EditClicked(p)),
            });
        }

        if (_canWriteEstimates)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "monitor",
                Label = "New estimate",
                OnClick = EventCallback.Factory.Create(this, () => NewEstimate(p.PropertyId)),
            });
        }

        if (CanAttachDocuments)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "attach_file",
                Label = "Attach documents",
                OnClick = EventCallback.Factory.Create(this, () => AttachDocuments(p.PropertyId)),
            });
        }

        // POST …/events is properties.update, and is offered on an archived property too.
        if (_canUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "history",
                Label = "New event",
                OnClick = EventCallback.Factory.Create(this, () => NewEvent(p.PropertyId)),
            });
        }

        items.Add(new OdsMenuItem
        {
            Icon = "fingerprint",
            Label = "Copy ID",
            TrailingIcon = "content_copy",
            OnClick = EventCallback.Factory.Create(this, () => Clipboard.CopyAsync(p.PropertyId.ToString(), "Property ID copied.")),
        });

        if (_canUpdate || _canDelete)
            items.Add(new OdsMenuItem { Divider = true });

        if (_canUpdate)
        {
            var archived = p.Archived is not null;
            items.Add(new OdsMenuItem
            {
                Icon = archived ? "unarchive" : "inventory_2",
                Label = archived ? "Restore" : "Archive",
                OnClick = EventCallback.Factory.Create(this, () => SetArchived(p, !archived)),
            });
        }

        if (_canDelete)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "delete",
                Label = "Delete",
                Danger = true,
                OnClick = EventCallback.Factory.Create(this, () => DeleteClicked(p)),
            });
        }

        return items;
    }

    // ── Create / edit ──────────────────────────────────────────────────────────────
    private void AddClicked()
    {
        _createKey = Guid.NewGuid();
        _createOpen = true;
    }

    private void EditClicked(ExistingProperty p)
    {
        _editProperty = p;
        _editKey = Guid.NewGuid();
        _editOpen = true;
    }

    private async Task OnPropertyCreated(ExistingProperty created)
    {
        await Task.WhenAll(LoadProperties(), LoadSummary());
        // Open the new record, so its estimates and smart tags — which a create cannot set — are the
        // next thing in view.
        _expandedId = created.PropertyId;
        await Flash(created.PropertyId);
    }

    // Null, not Guid.Empty, for a property whose menu never asked: a dictionary miss on a value type
    // yields Guid.Empty, which the section would read as a fresh request and open the dialog on expand.
    private Guid? NewEstimateTokenFor(Guid id) => _newEstimateTokens.TryGetValue(id, out var token) ? token : null;

    private Guid? AttachTokenFor(Guid id) => _attachTokens.TryGetValue(id, out var token) ? token : null;

    private void AttachDocuments(Guid id)
    {
        // Expand first, as for a new estimate: the dialog writes into a section that has to be in view.
        _expandedId = id;
        _attachTokens[id] = Guid.NewGuid();
    }

    private void NewEstimate(Guid id)
    {
        // Expand first: the dialog writes into a section the reader has to be able to see the result in.
        _expandedId = id;
        _newEstimateTokens[id] = Guid.NewGuid();
    }

    private Guid? NewEventTokenFor(Guid id) => _newEventTokens.TryGetValue(id, out var token) ? token : null;

    private void NewEvent(Guid id)
    {
        _expandedId = id;
        _newEventTokens[id] = Guid.NewGuid();
    }

    /// <summary>
    /// Routes a section's sentence into the page's one <c>OdsLiveAnnouncer</c> — two live regions on one
    /// page race each other, so the events section raises text rather than owning a region.
    /// </summary>
    private void Announce(string message)
    {
        _announce = message;
        StateHasChanged();
    }

    // ── Archive / delete ───────────────────────────────────────────────────────────
    private async Task SetArchived(ExistingProperty p, bool archived)
    {
        if (!_canUpdate)
            return;

        var ok = (await Properties.UpdateAsync(p.PropertyId, PropertyWrites.WithArchived(p, archived)))
            .Toast(Snackbar, archived ? "Unable to archive property" : "Unable to restore property",
                archived ? "Property archived." : "Property restored.");
        if (ok)
            await ReloadProperty(p.PropertyId);
    }

    private void DeleteClicked(ExistingProperty p)
    {
        _deleteProperty = p;
        _deleteKey = Guid.NewGuid();
        _deleteOpen = true;
    }

    private async Task DeleteAsync(ExistingProperty p)
    {
        if (!_canDelete)
            return;

        if (!(await Properties.DeleteAsync(p.PropertyId)).Toast(Snackbar, "Unable to delete property", "Property deleted."))
            return;

        _properties.RemoveAll(x => x.PropertyId == p.PropertyId);
        _newEstimateTokens.Remove(p.PropertyId);
        _attachTokens.Remove(p.PropertyId);
        _documentCounts.Remove(p.PropertyId);
        _newEventTokens.Remove(p.PropertyId);
        if (_expandedId == p.PropertyId)
            _expandedId = null;
        if (_editProperty?.PropertyId == p.PropertyId)
        {
            _editProperty = null;
            _editOpen = false;
        }

        await LoadSummary();
    }

    // ── Header signal: "Attention" — missing exchange rates ─────────────────────
    //
    // The Accounts rollup applied to the estimated-value total: one warning row per owned property
    // whose estimate is in a currency with no rate to the main one. The server names them
    // (PropertyValueSummary.UnconvertedProperties); the value block — and with it this list — is null
    // without properties.estimates.read, so a caller who cannot see values is never told one is
    // missing. Intersected with the loaded list, which is server-filtered, so View never jumps to a
    // record the filters hide.
    private List<PageHeaderProblem> HeaderProblems =>
        [.. RateGapProblems(
            _summary,
            _properties.Select(p => p.PropertyId).ToHashSet(),
            id => EventCallback.Factory.Create(this, () => JumpTo(id)))];

    /// <summary>
    /// One warning row per gap the list shows, led by the property's name and ungrouped — it is the only
    /// kind this panel carries — its View bound by <paramref name="onView"/>.
    /// </summary>
    internal static IEnumerable<PageHeaderProblem> RateGapProblems(
        PropertySummary? summary, IReadOnlySet<Guid> listed, Func<Guid, EventCallback> onView) =>
        RateGaps(summary, listed).Select(gap => new PageHeaderProblem
        {
            Severity = PageHeaderSeverity.Warning,
            Lead = gap.Name,
            Message = RateGapSummary(gap, summary!.Value!.BaseCurrency),
            OnView = onView(gap.PropertyId),
        });

    /// <summary>The summary's unconverted properties that the loaded list also shows, in the server's order.</summary>
    internal static IEnumerable<UnconvertedProperty> RateGaps(PropertySummary? summary, IReadOnlySet<Guid> listed) =>
        (summary?.Value?.UnconvertedProperties ?? []).Where(gap => listed.Contains(gap.PropertyId));

    private UnconvertedProperty? RateGapFor(Guid propertyId) =>
        _summary?.Value?.UnconvertedProperties.FirstOrDefault(gap => gap.PropertyId == propertyId);

    /// <summary>The header row: the pair and what the property was left out of.</summary>
    internal static string RateGapSummary(UnconvertedProperty gap, string baseCurrency) =>
        $"No {gap.CurrencyCode} → {baseCurrency} rate, so this property is left out of the estimated value total.";

    /// <summary>The record alert's detail: why conversion matters here and where the fix is.</summary>
    internal static string RateGapDetail(UnconvertedProperty gap, string baseCurrency) =>
        $"Odyssey converts every property’s estimated value into your main currency ({baseCurrency}) to total "
        + $"what the owned properties are worth. There’s no {gap.CurrencyCode} → {baseCurrency} rate stored, so "
        + "this property is left out of that total. Add the rate on the Exchange rates page to include it.";

    // The contextual fix for a missing-rate problem: the Exchange rates page, as on Accounts.
    private void GoToExchangeRates() => NavigationManager.NavigateTo("exchange-rates");

    // Header-rollup → property: open the record, scroll it into view, flash the attention ring.
    private async Task JumpTo(Guid id)
    {
        _expandedId = id;
        _flashId = id;
        StateHasChanged();

        await OdsRecordReveal.ScrollIntoViewAsync(ScrollManager, $"#prop-{id}");
        await OdsRecordReveal.EndFlashAsync(() => _flashId == id, EndFlash);
    }

    private void EndFlash()
    {
        _flashId = null;
        StateHasChanged();
    }

    // Flash without a scroll: the record the action just created or changed is already in view.
    private async Task Flash(Guid id)
    {
        _flashId = id;
        StateHasChanged();
        await OdsRecordReveal.EndFlashAsync(() => _flashId == id, EndFlash);
    }

    // ── Card header ────────────────────────────────────────────────────────────────
    private static IReadOnlyList<RenderFragment?> MetaFor(ExistingProperty p, PropertyKindInfo kind)
    {
        var where = p.Type == PropertyType.Vehicle
            ? string.Join(" ", new[]
            {
                p.VehicleDetails?.Make,
                p.VehicleDetails?.Model,
                p.VehicleDetails?.ModelYear?.ToString(CultureInfo.InvariantCulture),
            }.Where(x => !string.IsNullOrWhiteSpace(x)))
            : string.Join(", ", new[] { p.RealEstateDetails?.City, p.RealEstateDetails?.CountryCode }
                .Where(x => !string.IsNullOrWhiteSpace(x)));

        return [OdsRecordMeta.Text(kind.Label), OdsRecordMeta.Text(p.Description), OdsRecordMeta.Text(where)];
    }

    private IReadOnlyList<OdsRecordCount> CountsFor(ExistingProperty p) => CountsFor(p, _canReadEstimates);

    /// <summary>
    /// The collapsed card's counts. Estimates follow <c>properties.estimates.read</c>; the contract
    /// count needs no claim of its own, because the server already withholds it (null) without
    /// <c>contracts.read</c> — and, as on an account, a property party to nothing states no count.
    /// </summary>
    internal static IReadOnlyList<OdsRecordCount> CountsFor(ExistingProperty p, bool canReadEstimates)
    {
        var counts = new List<OdsRecordCount>(4);
        if (canReadEstimates)
            counts.Add(new OdsRecordCount("monitor", (p.EstimateCount ?? 0).ToString(CultureInfo.CurrentCulture), "Estimates"));

        counts.Add(new OdsRecordCount("sell", p.SmartTagCount.ToString(CultureInfo.CurrentCulture), "Smart tags"));
        counts.Add(new OdsRecordCount("history", p.EventCount.ToString(CultureInfo.CurrentCulture), "Events"));

        if (p.ContractCount is > 0 and var contractCount)
            counts.Add(new OdsRecordCount("handshake", contractCount.ToString(CultureInfo.CurrentCulture), "Contracts"));

        return counts;
    }

    private string? FigureCaption(ExistingProperty p) =>
        !_canReadEstimates ? null
        : p.CurrentEstimatedValueEffectiveFrom is { } from
            ? $"estimated · {from.ToString("MMM yyyy", CultureInfo.InvariantCulture)}"
            : "no estimate";

    // A worth in force on a property still owned reads in the income hue; a disposed or archived
    // one keeps its figure but mutes it, since it no longer describes something the household holds.
    private OdsRecordFigureTone FigureTone(ExistingProperty p) =>
        _canReadEstimates && p.Status == PropertyStatus.Owned && p.CurrentEstimatedValue is not null
            ? OdsRecordFigureTone.Income
            : OdsRecordFigureTone.Muted;

    // ── Detail helpers ─────────────────────────────────────────────────────────────
    private static bool HasVehicleTiles(VehicleDetailsDto v) =>
        !string.IsNullOrWhiteSpace(v.RegistrationNumber) || !string.IsNullOrWhiteSpace(v.Vin)
        || !string.IsNullOrWhiteSpace(v.Make) || !string.IsNullOrWhiteSpace(v.Model)
        || v.ModelYear is not null || v.FirstRegisteredDate is not null;

    internal static bool HasRealEstateTiles(ExistingProperty property, RealEstateDetailsDto re) =>
        PropertyVisuals.AddressText(re) is not null || !string.IsNullOrWhiteSpace(re.CadastralNumber)
        || property.HomeownerAssociation is not null
        || re.LivingAreaSqm is not null || re.PlotAreaSqm is not null || re.BuildYear is not null;

    /// <summary>
    /// The homeowner-association tile's foot (issue #217): the contact type, or — for an archived
    /// contact — that state and since when, so it is stated in text and never by tone alone.
    /// </summary>
    internal static string AssociationFoot(PropertyHomeownerAssociation association) =>
        association.Archived is { } archived ? $"Archived contact · since {LongDate(archived)}" : "Organization";

    private static readonly OdsTypeOption OrganizationMeta =
        OdsTypeRegistries.ContactTypeOf(nameof(Odyssey.Dtos.ContactType.Organization));

    private static string Area(decimal sqm) => $"{sqm.ToString("#,##0.#", CultureInfo.InvariantCulture)} m²";

    private static string LongDate(DateTime date) => date.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture);

    private string? CurrencyName(string code) =>
        _currenciesByCode.TryGetValue(code, out var currency) ? currency.Name : null;

    private string FormatMoney(decimal value, string? currencyCode) =>
        OdsMoney.Format(value, currencyCode,
            OdsMoney.MinorUnitsOf(currencyCode is null ? null : _currenciesByCode.GetValueOrDefault(currencyCode)));

    public void Dispose() => _listLoader.Dispose();
}
