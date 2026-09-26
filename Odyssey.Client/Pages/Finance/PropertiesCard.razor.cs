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
    private bool _loadError;
    private string _announce = "";
    private Guid? _expandedId;

    /// <summary>One token per property whose row menu asked for a New estimate; see
    /// <see cref="PropertyEstimatesSection.NewEstimateRequestToken"/>.</summary>
    private readonly Dictionary<Guid, Guid> _newEstimateTokens = new();

    // ── Persisted page state ───────────────────────────────────────────────────
    private const string PageStateKey = "properties-page";
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
    }

    // ── Page-state persistence ─────────────────────────────────────────────────
    private Task RestorePageStateAsync() =>
        PageState.RestoreOrSeedAsync<PropertiesPageState>(PageStateKey, ApplyPageState, BuildPageState);

    private void ApplyPageState(PropertiesPageState state)
    {
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
        var result = await Properties.ListAllAsync(
            _searchString,
            _typeFilter,
            _statusFilter,
            _sort.Key,
            _sort.Dir == OdsSortDirection.Asc ? "asc" : "desc");

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

    private void OnSmartTagCountChanged(ExistingProperty property, int count)
    {
        property.SmartTagCount = count;
        StateHasChanged();
    }

    // ── Expand ──────────────────────────────────────────────────────────────────
    private bool IsExpanded(Guid id) => _expandedId == id;

    private void ToggleExpand(Guid id) => _expandedId = _expandedId == id ? null : id;

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

    private void NewEstimate(Guid id)
    {
        // Expand first: the dialog writes into a section the reader has to be able to see the result in.
        _expandedId = id;
        _newEstimateTokens[id] = Guid.NewGuid();
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
        if (_expandedId == p.PropertyId)
            _expandedId = null;
        if (_editProperty?.PropertyId == p.PropertyId)
        {
            _editProperty = null;
            _editOpen = false;
        }

        await LoadSummary();
    }

    private async Task Flash(Guid id)
    {
        _flashId = id;
        StateHasChanged();
        await Task.Delay(OdsTiming.RowFlashMs);
        if (_flashId == id)
        {
            _flashId = null;
            StateHasChanged();
        }
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

    private IReadOnlyList<OdsRecordCount> CountsFor(ExistingProperty p)
    {
        var counts = new List<OdsRecordCount>(3);
        if (_canReadEstimates)
            counts.Add(new OdsRecordCount("monitor", (p.EstimateCount ?? 0).ToString(CultureInfo.CurrentCulture), "Estimates"));

        counts.Add(new OdsRecordCount("sell", p.SmartTagCount.ToString(CultureInfo.CurrentCulture), "Smart tags"));

        // Null without contracts.read — the server withholds it rather than zeroing it — and, as on
        // an account, a property party to nothing states no count (issue #208).
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

    private static bool HasRealEstateTiles(RealEstateDetailsDto re) =>
        PropertyVisuals.AddressText(re) is not null || !string.IsNullOrWhiteSpace(re.CadastralNumber)
        || re.LivingAreaSqm is not null || re.PlotAreaSqm is not null || re.BuildYear is not null;

    private static string Area(decimal sqm) => $"{sqm.ToString("#,##0.#", CultureInfo.InvariantCulture)} m²";

    private static string LongDate(DateTime date) => date.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture);

    private string? CurrencyName(string code) =>
        _currenciesByCode.TryGetValue(code, out var currency) ? currency.Name : null;

    private string FormatMoney(decimal value, string? currencyCode) =>
        OdsMoney.Format(value, currencyCode,
            OdsMoney.MinorUnitsOf(currencyCode is null ? null : _currenciesByCode.GetValueOrDefault(currencyCode)));
}
